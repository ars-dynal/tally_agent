using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TallyAgent.Core.Notifications;
using static TallyAgent.Core.Tally.TallyXml;

namespace TallyAgent.Core.Tally.Extractors;

using Row = Dictionary<string, object?>;

/// <summary>Snapshot reports: Trial Balance, Balance Sheet, P&amp;L, Stock Summary,
/// outstanding payables/receivables. Handles both flat-sibling and nested layouts
/// and does not assume report rows are direct children of the XML root.</summary>
public sealed class ReportExtractor(TallyClient client, ILogger<ReportExtractor> log)
{
    // Snapshot reports are full-financial-year computations — the heaviest
    // requests the agent makes. They get their own (longer) budget and are
    // NEVER retried at the same size within a cycle: a timeout defers the
    // snapshot to the next snapshot slot instead of queuing more work on Tally.
    private Task<XDocument> PostReport(string envelope, CancellationToken ct) =>
        client.PostAsync(envelope, client.SnapshotRequestTimeout, maxTimeoutRetries: 0, ct);

    /// <summary>Refusals seen this cycle, for the engine to report as warnings.
    /// A refused report that has a working fallback must be LOUD but not fatal:
    /// the dataset still produces rows, and the reason it took the long way
    /// round has to reach a human.</summary>
    private readonly List<string> _refusals = [];
    public IReadOnlyList<string> RefusalsThisCycle => _refusals;

    /// <summary>
    /// POST a report, returning null when Tally REFUSED it.
    ///
    /// v2.2.0 made a refusal throw centrally, which was right — a refusal must
    /// never be mistaken for an empty report. But it threw straight past the
    /// fallback, so trial_balance went from 1,038 reconciling rows to ZERO
    /// batches the moment Tally started refusing the report. Detection and
    /// fallback are different decisions: the refusal is still detected, named
    /// and surfaced, and then the extractor takes the route it already had.
    ///
    /// Reports with NO fallback keep using <see cref="PostReport"/> and still
    /// fail hard, which is correct — there is nothing else to try.
    /// </summary>
    private async Task<XDocument?> PostReportAllowingRefusal(string envelope, string reportName,
        CancellationToken ct)
    {
        try
        {
            return await PostReport(envelope, ct);
        }
        catch (TallyException ex) when (ex.Category == ErrorCategory.TallyRequestRejected)
        {
            var message = $"Tally refused the '{reportName}' report ({ex.Message}) — " +
                          "falling back to the collection route. The rows are derived, not " +
                          "reported: check the source column.";
            log.LogWarning("{Message}", message);
            _refusals.Add(message);
            return null;
        }
    }

    // Ledger closing balances as-of a date: shared by the two outstanding
    // datasets and the trial-balance fallback (one Tally request per cycle
    // instead of three).
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private (DateOnly To, XDocument Doc)? _ledgerBalances;
    /// <summary>The Bills collection is shared by the payable and receivable
    /// fallbacks — one request per cycle, not one per dataset (same discipline
    /// as the Ledger/StockItem caches: never ask Tally the same thing twice).</summary>
    private (DateOnly From, DateOnly To, XDocument Doc)? _billsCollection;

    public void BeginCycle() { EndCycle(); _refusals.Clear(); }
    public void EndCycle() { _ledgerBalances = null; _billsCollection = null; }

    private async Task<XDocument> LedgerBalancesDocument(DateOnly to, CancellationToken ct)
    {
        if (_ledgerBalances is { } c && c.To == to) return c.Doc;
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_ledgerBalances is { } c2 && c2.To == to) return c2.Doc;
            var doc = await PostReport(TallyEnvelopes.Collection(
                "Ledger", ["NAME", "PARENT", "CLOSINGBALANCE"], client.Company, null, to), ct);
            _ledgerBalances = (to, doc);
            return doc;
        }
        finally { _cacheLock.Release(); }
    }

    private static bool Is(XElement el, string localName) =>
        el.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    private static XElement? Child(XElement el, string localName) =>
        el.Elements().FirstOrDefault(e => Is(e, localName));

    /// <summary>DSPACCNAME → DSPDISPNAME (TallyPrime) or element text (legacy).</summary>
    private static string DspName(XElement el)
    {
        var disp = Child(el, "DSPDISPNAME")?.Value;
        return (disp ?? el.Value ?? "").Trim();
    }

    public async Task<List<Row>> TrialBalance(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await PostReportAllowingRefusal(
            TallyEnvelopes.Report("Trial Balance", from, to, client.Company), "Trial Balance", ct);
        var rows = doc is null ? [] : ParseTrialBalanceReport(doc);
        if (rows.Count == 0)
            rows = await TrialBalanceFromLedgers(from, to, ct);
        log.LogInformation("Trial balance: {N} rows from {Source}", rows.Count, SourceOf(rows));
        return rows;
    }

    /// <summary>
    /// Parse a "Trial Balance" report export.
    ///
    /// Verified against Tally's own UI export (TrialBal.xml, 11 primary groups):
    ///
    ///     &lt;DSPACCNAME&gt;&lt;DSPDISPNAME&gt;Current Liabilities&lt;/DSPDISPNAME&gt;&lt;/DSPACCNAME&gt;
    ///     &lt;DSPACCINFO&gt;
    ///       &lt;DSPCLDRAMT&gt;&lt;DSPCLDRAMTA&gt;-110996207.04&lt;/DSPCLDRAMTA&gt;&lt;/DSPCLDRAMT&gt;
    ///       &lt;DSPCLCRAMT&gt;&lt;DSPCLCRAMTA&gt;56888982.54&lt;/DSPCLCRAMTA&gt;&lt;/DSPCLCRAMT&gt;
    ///     &lt;/DSPACCINFO&gt;
    ///
    /// DEBITS ARE NEGATIVE, and an empty amount element means "nothing on this
    /// side". The pre-v2.3.0 code looked for DSPCLDR / DSPCLCR / BSMAINAMT, none
    /// of which appear here - so if this report had ever answered, it would have
    /// produced 11 rows of zeros rather than no rows at all.
    ///
    /// Public so the verification tool can run the reference export through the
    /// exact code the agent uses.
    /// </summary>
    public static List<Row> ParseTrialBalanceReport(XDocument doc) =>
        WalkNameAmountPairs(doc, (name, amtEl) =>
        {
            var dr = NumByLocalName(amtEl, "DSPCLDRAMTA");
            var cr = NumByLocalName(amtEl, "DSPCLCRAMTA");
            // Other layouts, kept as fallbacks WITHIN the report route.
            if (dr == 0) dr = NumByLocalName(amtEl, "DSPCLDR");
            if (cr == 0) cr = NumByLocalName(amtEl, "DSPCLCR");
            if (dr == 0 && cr == 0) dr = NumByLocalName(amtEl, "BSMAINAMT");
            var debit = Math.Abs(dr);
            var credit = Math.Abs(cr);
            return new Row
            {
                ["ledger_name"] = name,
                ["closing_debit"] = debit,
                ["closing_credit"] = credit,
                // Debit-positive, matching the ledger fallback's -closing.
                ["net_amount"] = debit - credit,
                ["parent_group"] = "",
                ["source"] = "report",
            };
        });

    /// <summary>Which route produced these rows, for the log line. The rows
    /// themselves carry it in <c>source</c>.</summary>
    private static string SourceOf(List<Row> rows) =>
        rows.Count > 0 && rows[0].TryGetValue("source", out var s) ? s as string ?? "?" : "nothing";

    /// <summary>Fallback when the "Trial Balance" report export yields no rows
    /// (report layouts vary across TallyPrime builds): derive the trial balance
    /// from a period-bound Ledger collection — CLOSINGBALANCE honours SVTODATE,
    /// so the figures are as-of the requested period end.</summary>
    private async Task<List<Row>> TrialBalanceFromLedgers(DateOnly from, DateOnly to, CancellationToken ct)
    {
        log.LogInformation("Trial balance report export was empty — falling back to dated Ledger collection");
        var doc = await LedgerBalancesDocument(to, ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants().Where(e => Is(e, "LEDGER")))
        {
            var name = Text(el, "NAME");
            if (name.Length == 0) continue;
            var closing = Num(el, "CLOSINGBALANCE");
            if (closing == 0) continue;
            // Tally sign convention: positive = credit, negative = debit.
            rows.Add(new Row
            {
                ["ledger_name"] = name,
                ["closing_debit"] = closing < 0 ? Math.Abs(closing) : 0,
                ["closing_credit"] = closing > 0 ? closing : 0,
                ["net_amount"] = -closing,
                ["parent_group"] = Text(el, "PARENT"),
                // NOT the "Trial Balance" report. Both routes derive from the
                // same ledger balances, so a fallback result reconciles to
                // Tally's screen exactly as the report would — which is why
                // serving the fallback for weeks would look like success. The
                // column is how anyone finds out.
                ["source"] = "ledger_collection",
            });
        }
        return rows;
    }

    public async Task<List<Row>> BalanceSheet(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await PostReportAllowingRefusal(
            TallyEnvelopes.Report("Balance Sheet", from, to, client.Company), "Balance Sheet", ct);
        var rows = doc is null ? [] : WalkNameAmountPairs(doc, (name, amtEl) =>
        {
            var amount = NumByLocalName(amtEl, "BSMAINAMT");
            if (amount == 0) amount = NumByLocalName(amtEl, "BSSUBAMT");
            return new Row
            {
                ["ledger_name"] = name,
                ["amount"] = amount,
                ["parent_group"] = "",
                ["category"] = amount >= 0 ? "Liabilities" : "Assets",
                ["source"] = "report",
            };
        });
        if (rows.Count == 0)
            rows = await GroupBalances(from, to, revenueGroups: false, ct);
        log.LogInformation("Balance sheet: {N} rows from {Source}", rows.Count, SourceOf(rows));
        return rows;
    }

    public async Task<List<Row>> ProfitLoss(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await PostReportAllowingRefusal(
            TallyEnvelopes.Report("Profit and Loss A/c", from, to, client.Company),
            "Profit and Loss A/c", ct);
        var rows = doc is null ? [] : WalkNameAmountPairs(doc, (name, amtEl) =>
        {
            var amount = NumByLocalName(amtEl, "PLAMT");
            if (amount == 0) amount = NumByLocalName(amtEl, "BSMAINAMT");
            return new Row
            {
                ["ledger_name"] = name,
                ["amount"] = amount,
                ["parent_group"] = "",
                ["source"] = "report",
            };
        });
        if (rows.Count == 0)
            rows = await GroupBalances(from, to, revenueGroups: true, ct);
        log.LogInformation("P&L: {N} rows from {Source}", rows.Count, SourceOf(rows));
        return rows;
    }

    /// <summary>Fallback for empty Balance Sheet / P&amp;L report exports:
    /// period-bound Group collection closing balances. Balance sheet keeps
    /// non-revenue groups; P&amp;L keeps revenue groups (ISREVENUE).</summary>
    private async Task<List<Row>> GroupBalances(DateOnly from, DateOnly to,
        bool revenueGroups, CancellationToken ct)
    {
        log.LogInformation("{Report} report export was empty — falling back to dated Group collection",
            revenueGroups ? "P&L" : "Balance sheet");
        var doc = await PostReport(TallyEnvelopes.Collection(
            "Group", ["NAME", "PARENT", "ISREVENUE", "CLOSINGBALANCE"], client.Company, from, to), ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants().Where(e => Is(e, "GROUP")))
        {
            var name = Text(el, "NAME");
            if (name.Length == 0) continue;
            if (Bool(el, "ISREVENUE") != revenueGroups) continue;
            var amount = Num(el, "CLOSINGBALANCE");
            if (amount == 0) continue;
            var row = new Row
            {
                ["ledger_name"] = name,
                ["amount"] = amount,
                ["parent_group"] = Text(el, "PARENT"),
                ["source"] = "group_collection",
            };
            if (!revenueGroups)
                row["category"] = amount >= 0 ? "Liabilities" : "Assets";
            rows.Add(row);
        }
        return rows;
    }

    public async Task<List<Row>> StockSummary(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await PostReportAllowingRefusal(
            TallyEnvelopes.Report("Stock Summary", from, to, client.Company), "Stock Summary", ct);
        var rows = new List<Row>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nameEl in (doc?.Descendants() ?? []).Where(e => Is(e, "DSPACCNAME")))
        {
            var name = DspName(nameEl);
            if (name.Length == 0 || !seen.Add(name)) continue;

            var info = FollowingSibling(nameEl) ?? nameEl.Parent;
            if (info is null) continue;
            var cl = info.Descendants().FirstOrDefault(e => Is(e, "DSPSTKCL")) ?? info;

            rows.Add(new Row
            {
                ["item_name"] = name,
                ["stock_group"] = "",
                ["opening_qty"] = QtyNum(info, "DSPSTKOPQTY", "DSPOPENQTY"),
                ["opening_value"] = NumByLocalName(info, "DSPSTKOPAMT"),
                ["inward_qty"] = QtyNum(info, "DSPSTKINQTY", "DSPINWQTY"),
                ["inward_value"] = NumByLocalName(info, "DSPSTKINAMT"),
                ["outward_qty"] = QtyNum(info, "DSPSTKOUTQTY", "DSPOUTQTY"),
                ["outward_value"] = NumByLocalName(info, "DSPSTKOUTAMT"),
                ["closing_qty"] = QtyNum(cl, "DSPCLQTY", "DSPSTKCLQTY"),
                ["closing_value"] = NumByLocalName(cl, "DSPCLAMTA") != 0
                    ? NumByLocalName(cl, "DSPCLAMTA") : NumByLocalName(cl, "DSPSTKCLAMT"),
                ["source"] = "report",
            });
        }
        if (rows.Count == 0)
            rows = await StockSummaryFromItems(from, to, ct);
        log.LogInformation("Stock summary: {N} rows from {Source}", rows.Count, SourceOf(rows));
        return rows;
    }

    /// <summary>Fallback for an empty "Stock Summary" report export: period-bound
    /// StockItem collection — closing qty/value honour SVTODATE.</summary>
    private async Task<List<Row>> StockSummaryFromItems(DateOnly from, DateOnly to, CancellationToken ct)
    {
        log.LogInformation("Stock summary report export was empty — falling back to dated StockItem collection");
        var doc = await PostReport(TallyEnvelopes.Collection(
            "StockItem",
            ["NAME", "PARENT", "OPENINGBALANCE", "OPENINGVALUE", "CLOSINGBALANCE", "CLOSINGVALUE"],
            client.Company, from, to), ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants().Where(e => Is(e, "STOCKITEM")))
        {
            var name = Text(el, "NAME");
            if (name.Length == 0) continue;
            var closingQty = Num(el, "CLOSINGBALANCE");
            var closingValue = Num(el, "CLOSINGVALUE");
            if (closingQty == 0 && closingValue == 0) continue;
            rows.Add(new Row
            {
                ["item_name"] = name,
                ["stock_group"] = Text(el, "PARENT"),
                ["opening_qty"] = Num(el, "OPENINGBALANCE"),
                ["opening_value"] = Num(el, "OPENINGVALUE"),
                ["inward_qty"] = 0d,
                ["inward_value"] = 0d,
                ["outward_qty"] = 0d,
                ["outward_value"] = 0d,
                ["closing_qty"] = closingQty,
                ["closing_value"] = closingValue,
                ["source"] = "stockitem_collection",
            });
        }
        return rows;
    }

    /// <summary>Outstanding = closing balances (as of <paramref name="asOf"/>)
    /// of ledgers under the given parent groups.</summary>
    public async Task<List<Row>> Outstanding(string parentGroupContains, DateOnly asOf, CancellationToken ct)
    {
        var doc = await LedgerBalancesDocument(asOf, ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants().Where(e => Is(e, "LEDGER")))
        {
            var parent = Text(el, "PARENT");
            if (!parent.Contains(parentGroupContains, StringComparison.OrdinalIgnoreCase)) continue;
            var amount = Num(el, "CLOSINGBALANCE");
            if (amount == 0) continue;
            rows.Add(new Row
            {
                ["party_name"] = Text(el, "NAME"),
                ["parent_group"] = parent,
                ["amount"] = amount,
            });
        }
        return rows;
    }

    // ── bill-level outstandings (v2.2.0) ──────────────────────────
    //
    // NEW datasets, additive. outstanding_payables / outstanding_receivables are
    // NOT touched: they are ledger CLOSINGBALANCE sums, they reconcile exactly to
    // the trial balance, and staging views are deployed against them. These two
    // answer the different question "which bills make up that balance".

    /// <summary>
    /// The record container in Tally's Bills Payable / Bills Receivable export.
    ///
    /// Verified against Tally's own UI export (Bills.xml, 351 records):
    ///
    ///     &lt;BILLFIXED&gt;
    ///       &lt;BILLDATE&gt;1-Nov-21&lt;/BILLDATE&gt;
    ///       &lt;BILLREF&gt;PO/21-22/00172&lt;/BILLREF&gt;
    ///       &lt;BILLPARTY&gt;Apar Industries Limited&lt;/BILLPARTY&gt;
    ///     &lt;/BILLFIXED&gt;
    ///     &lt;BILLCL&gt;-369.00&lt;/BILLCL&gt;
    ///     &lt;BILLDUE&gt;1-Nov-21&lt;/BILLDUE&gt;
    ///     &lt;BILLOVERDUE&gt;1768&lt;/BILLOVERDUE&gt;
    ///
    /// The amount, due date and overdue count are FOLLOWING SIBLINGS of
    /// BILLFIXED, not children of it. Searching inside the container - which is
    /// what the first cut did - finds the reference and the party but never the
    /// amount, and yields 351 rows of 0.00 that look like a working extraction.
    /// </summary>
    private const string BillRecordContainer = "BILLFIXED";

    /// <summary>Amounts are negative for credit and are kept EXACTLY as Tally
    /// reports them; the sign is information, not a formatting accident.</summary>
    private static readonly string[] BillAmountTags =
        ["BILLCL", "BILLAMT", "DSPBILLAMT", "CLOSINGBALANCE"];
    private static readonly string[] BillRefTags = ["BILLREF", "BILLNAME", "NAME"];
    private static readonly string[] BillDateTags = ["BILLDATE", "DSPBILLDATE"];
    private static readonly string[] BillPartyTags =
        ["BILLPARTY", "PARTYNAME", "PARTYLEDGERNAME", "LEDGERNAME", "DSPDISPNAME"];
    private static readonly string[] BillDueTags = ["BILLDUE", "BILLDUEDATE", "DUEDATE"];
    private static readonly string[] BillOverdueTags = ["BILLOVERDUE", "OVERDUEDAYS", "BILLAGE"];

    /// <summary>
    /// Bill-level outstandings from Tally's own "Bills Payable" / "Bills
    /// Receivable" report: date, reference, party, pending amount, due date and
    /// TALLY'S OWN overdue-day count (kept as reported, never recomputed — the
    /// due date depends on the credit period and the bill type, and Tally is the
    /// authority on both).
    ///
    /// Falls back to the Bills collection when the report export is empty, the
    /// same pattern the other reports here use. The fallback cannot carry an
    /// overdue-day count (report-only column), so it leaves overdue_days null
    /// rather than inventing one; every row records which path produced it in
    /// <c>source</c>.
    /// </summary>
    public async Task<List<Row>> Bills(string reportName, string parentGroupContains,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await PostReportAllowingRefusal(
            TallyEnvelopes.BillsReport(reportName, from, to, client.Company), reportName, ct);
        var rows = doc is null ? [] : ParseBillsReport(doc, to);
        if (rows.Count == 0)
            rows = await BillsFromCollection(parentGroupContains, from, to, ct);
        log.LogInformation("{Report}: {N} rows from {Source}", reportName, rows.Count, SourceOf(rows));
        return rows;
    }

    /// <summary>Public so <c>TallyAgent.Cli verify-bills</c> can parse a captured
    /// response and report what matched without a second request to Tally.</summary>
    public static List<Row> ParseBillsReport(XDocument doc, DateOnly asOf)
    {
        var rows = new List<Row>();
        foreach (var el in doc.Descendants().Where(e => Is(e, BillRecordContainer)))
        {
            // A record is the container PLUS the siblings that follow it, up to
            // the next container. Both the flat layout Tally actually emits and
            // a nested one therefore read the same way.
            var scope = RecordScope(el);

            var billRef = FirstText(scope, BillRefTags);
            var amount = FirstNum(scope, BillAmountTags);
            if (billRef.Length == 0 && amount == 0) continue;

            rows.Add(new Row
            {
                ["party_name"] = FirstText(scope, BillPartyTags),
                ["bill_ref"] = billRef,
                ["bill_date"] = FirstDate(scope, BillDateTags),
                ["pending_amount"] = amount,
                ["due_date"] = FirstDate(scope, BillDueTags),
                ["overdue_days"] = FirstOverdueDays(scope),
                ["as_of_date"] = asOf.ToString("yyyy-MM-dd"),
                ["source"] = "report",
            });
        }
        return rows;
    }

    /// <summary>The elements belonging to one bill record: the container and
    /// every following sibling up to the next container.</summary>
    private static List<XElement> RecordScope(XElement container)
    {
        var scope = new List<XElement> { container };
        for (var sib = container.NextNode as XElement; sib is not null;
             sib = sib.NextNode as XElement)
        {
            if (Is(sib, BillRecordContainer)) break;
            scope.Add(sib);
        }
        return scope;
    }

    /// <summary>Fallback: the period-bound Bills collection. Payable vs
    /// receivable is decided by the PARTY's group, read from the Ledger
    /// collection this cycle has already cached — no extra Tally request.</summary>
    private async Task<List<Row>> BillsFromCollection(string parentGroupContains,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        log.LogInformation("Bills report export was empty — falling back to the dated Bills collection");
        var ledgerDoc = await LedgerBalancesDocument(to, ct);
        var groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in ledgerDoc.Descendants().Where(e => Is(e, "LEDGER")))
        {
            var name = Text(el, "NAME");
            if (name.Length > 0) groupOf[name] = Text(el, "PARENT");
        }

        var doc = await BillsDocument(from, to, ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants().Where(e => Is(e, "BILLS") || Is(e, "BILL")))
        {
            var party = Text(el, "PARENT");
            if (party.Length == 0) continue;
            if (!groupOf.TryGetValue(party, out var group) ||
                !group.Contains(parentGroupContains, StringComparison.OrdinalIgnoreCase)) continue;

            var amount = Num(el, "CLOSINGBALANCE");
            if (amount == 0) continue;

            var billDate = Date(el, "BILLDATE");
            var creditDays = (int)Num(el, "BILLCREDITPERIOD");
            rows.Add(new Row
            {
                ["party_name"] = party,
                ["bill_ref"] = Text(el, "NAME"),
                ["bill_date"] = billDate,
                ["pending_amount"] = amount,
                // Tally's own due date is a report column; here it is only
                // derivable when a credit period is actually recorded.
                ["due_date"] = billDate is not null && creditDays > 0 &&
                               DateOnly.TryParse(billDate, out var bd)
                    ? bd.AddDays(creditDays).ToString("yyyy-MM-dd") : null,
                // Never computed — Tally's figure does not exist on this path.
                ["overdue_days"] = null,
                ["as_of_date"] = to.ToString("yyyy-MM-dd"),
                ["source"] = "bills_collection",
            });
        }
        return rows;
    }

    /// <summary>The Bills collection for this cycle (fetched once and shared by
    /// the payable and receivable fallbacks).</summary>
    private async Task<XDocument> BillsDocument(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (_billsCollection is { } c && c.From == from && c.To == to) return c.Doc;
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_billsCollection is { } c2 && c2.From == from && c2.To == to) return c2.Doc;
            var doc = await PostReport(TallyEnvelopes.BillsCollection(client.Company, from, to), ct);
            _billsCollection = (from, to, doc);
            return doc;
        }
        finally { _cacheLock.Release(); }
    }

    /// <summary>Tally's overdue-day count, kept verbatim. An EMPTY element stays
    /// null rather than becoming 0 - 38 of the 351 reference records have no
    /// overdue value, and "0" (due today) is a different fact from blank (not
    /// yet due).</summary>
    private static long? FirstOverdueDays(List<XElement> scope)
    {
        foreach (var tag in BillOverdueTags)
        {
            var target = Find(scope, tag);
            if (target is null) continue;
            var raw = target.Value.Trim();
            if (raw.Length == 0) return null;
            var digits = new string(raw.Where(c => char.IsDigit(c) || c == '-').ToArray());
            if (digits.Length > 0 && long.TryParse(digits, out var days)) return days;
        }
        return null;
    }

    private static XElement? Find(List<XElement> scope, string tag) =>
        scope.SelectMany(e => e.DescendantsAndSelf()).FirstOrDefault(e => Is(e, tag));

    private static string FirstText(List<XElement> scope, string[] tags)
    {
        foreach (var tag in tags)
        {
            var value = Find(scope, tag)?.Value.Trim();
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return "";
    }

    private static double FirstNum(List<XElement> scope, string[] tags)
    {
        foreach (var tag in tags)
        {
            var target = Find(scope, tag);
            if (target is null) continue;
            var v = TallyXml.Num(new XElement("X", new XElement(tag, target.Value)), tag);
            if (v != 0) return v;
        }
        return 0;
    }

    private static string? FirstDate(List<XElement> scope, string[] tags)
    {
        foreach (var tag in tags)
        {
            var target = Find(scope, tag);
            if (target is null) continue;
            var iso = TallyXml.Date(new XElement("X", new XElement(tag, target.Value)), tag);
            if (iso is not null) return iso;
        }
        return null;
    }

    private static XElement? FollowingSibling(XElement el)
    {
        var parent = el.Parent;
        if (parent is null) return null;
        var siblings = parent.Elements().ToList();
        var index = siblings.IndexOf(el);
        return index >= 0 && index + 1 < siblings.Count ? siblings[index + 1] : null;
    }

    private static double NumByLocalName(XElement el, string tag)
    {
        var target = el.Elements().FirstOrDefault(e => Is(e, tag))
            ?? el.Descendants().FirstOrDefault(e => Is(e, tag));
        if (target is null) return 0;
        return TallyXml.Num(new XElement("X", new XElement(tag, target.Value)), tag);
    }

    private static double QtyNum(XElement el, params string[] tags)
    {
        foreach (var tag in tags)
        {
            var target = el.DescendantsAndSelf().FirstOrDefault(e => Is(e, tag));
            if (target is null) continue;
            var v = TallyXml.Num(new XElement("X", new XElement(tag, target.Value)), tag);
            if (v != 0) return v;
        }
        return 0;
    }

    private static List<Row> WalkNameAmountPairs(XDocument doc, Func<string, XElement, Row> mapPair)
    {
        var rows = new List<Row>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nameEl in doc.Descendants().Where(e => Is(e, "DSPACCNAME")))
        {
            var name = DspName(nameEl);
            if (name.Length == 0 || !seen.Add(name)) continue;

            XElement? amountContainer = null;
            var parent = nameEl.Parent;
            if (parent is not null && (Is(parent, "DSPTOTINFO") || Is(parent, "DSPACCINFO")))
                amountContainer = parent;
            else
                amountContainer = FollowingSibling(nameEl) ?? parent;

            if (amountContainer is not null)
                rows.Add(mapPair(name, amountContainer));
        }

        return rows;
    }
}
