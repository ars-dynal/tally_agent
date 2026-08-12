using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using static TallyAgent.Core.Tally.TallyXml;

namespace TallyAgent.Core.Tally.Extractors;

using Row = Dictionary<string, object?>;

/// <summary>Snapshot reports: Trial Balance, Balance Sheet, P&amp;L, Stock Summary,
/// outstanding payables/receivables. Handles both flat-sibling and nested layouts
/// and does not assume report rows are direct children of the XML root.</summary>
public sealed class ReportExtractor(TallyClient client, ILogger<ReportExtractor> log)
{
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
        var doc = await client.PostAsync(
            TallyEnvelopes.Report("Trial Balance", from, to, client.Company), ct);
        var rows = WalkNameAmountPairs(doc, (name, amtEl) =>
        {
            var dr = Child(amtEl, "DSPCLDR") is not null ? NumByLocalName(amtEl, "DSPCLDR") : 0;
            var cr = Child(amtEl, "DSPCLCR") is not null ? NumByLocalName(amtEl, "DSPCLCR") : 0;
            if (dr == 0 && cr == 0) dr = NumByLocalName(amtEl, "BSMAINAMT");
            return new Row
            {
                ["ledger_name"] = name,
                ["closing_debit"] = Math.Abs(dr),
                ["closing_credit"] = Math.Abs(cr),
                ["net_amount"] = dr - cr,
                ["parent_group"] = "",
            };
        });
        if (rows.Count == 0)
            rows = await TrialBalanceFromLedgers(from, to, ct);
        log.LogInformation("Trial balance: {N} rows", rows.Count);
        return rows;
    }

    /// <summary>Fallback when the "Trial Balance" report export yields no rows
    /// (report layouts vary across TallyPrime builds): derive the trial balance
    /// from a period-bound Ledger collection — CLOSINGBALANCE honours SVTODATE,
    /// so the figures are as-of the requested period end.</summary>
    private async Task<List<Row>> TrialBalanceFromLedgers(DateOnly from, DateOnly to, CancellationToken ct)
    {
        log.LogInformation("Trial balance report export was empty — falling back to dated Ledger collection");
        var doc = await client.PostAsync(TallyEnvelopes.Collection(
            "Ledger", ["NAME", "PARENT", "CLOSINGBALANCE"], client.Company, from, to), ct);
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
            });
        }
        return rows;
    }

    public async Task<List<Row>> BalanceSheet(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Report("Balance Sheet", from, to, client.Company), ct);
        var rows = WalkNameAmountPairs(doc, (name, amtEl) =>
        {
            var amount = NumByLocalName(amtEl, "BSMAINAMT");
            if (amount == 0) amount = NumByLocalName(amtEl, "BSSUBAMT");
            return new Row
            {
                ["ledger_name"] = name,
                ["amount"] = amount,
                ["parent_group"] = "",
                ["category"] = amount >= 0 ? "Liabilities" : "Assets",
            };
        });
        if (rows.Count == 0)
            rows = await GroupBalances(from, to, revenueGroups: false, ct);
        log.LogInformation("Balance sheet: {N} rows", rows.Count);
        return rows;
    }

    public async Task<List<Row>> ProfitLoss(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Report("Profit and Loss A/c", from, to, client.Company), ct);
        var rows = WalkNameAmountPairs(doc, (name, amtEl) =>
        {
            var amount = NumByLocalName(amtEl, "PLAMT");
            if (amount == 0) amount = NumByLocalName(amtEl, "BSMAINAMT");
            return new Row
            {
                ["ledger_name"] = name,
                ["amount"] = amount,
                ["parent_group"] = "",
            };
        });
        if (rows.Count == 0)
            rows = await GroupBalances(from, to, revenueGroups: true, ct);
        log.LogInformation("P&L: {N} rows", rows.Count);
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
        var doc = await client.PostAsync(TallyEnvelopes.Collection(
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
            };
            if (!revenueGroups)
                row["category"] = amount >= 0 ? "Liabilities" : "Assets";
            rows.Add(row);
        }
        return rows;
    }

    public async Task<List<Row>> StockSummary(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Report("Stock Summary", from, to, client.Company), ct);
        var rows = new List<Row>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nameEl in doc.Descendants().Where(e => Is(e, "DSPACCNAME")))
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
            });
        }
        if (rows.Count == 0)
            rows = await StockSummaryFromItems(from, to, ct);
        log.LogInformation("Stock summary: {N} rows", rows.Count);
        return rows;
    }

    /// <summary>Fallback for an empty "Stock Summary" report export: period-bound
    /// StockItem collection — closing qty/value honour SVTODATE.</summary>
    private async Task<List<Row>> StockSummaryFromItems(DateOnly from, DateOnly to, CancellationToken ct)
    {
        log.LogInformation("Stock summary report export was empty — falling back to dated StockItem collection");
        var doc = await client.PostAsync(TallyEnvelopes.Collection(
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
            });
        }
        return rows;
    }

    /// <summary>Outstanding = closing balances of ledgers under the given parent groups.</summary>
    public async Task<List<Row>> Outstanding(string parentGroupContains, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Collection("Ledger", ["NAME", "PARENT", "CLOSINGBALANCE"], client.Company), ct);
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

    /// <summary>Bank ledger names (parents containing "Bank") for bank-book fan-out.</summary>
    public async Task<HashSet<string>> BankLedgerNames(CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Collection("Ledger", ["NAME", "PARENT"], client.Company), ct);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in doc.Descendants().Where(e => Is(e, "LEDGER")))
        {
            var parent = Text(el, "PARENT");
            if (parent.Contains("Bank", StringComparison.OrdinalIgnoreCase))
                set.Add(Text(el, "NAME"));
        }
        return set;
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

    private List<Row> WalkNameAmountPairs(XDocument doc, Func<string, XElement, Row> mapPair)
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
