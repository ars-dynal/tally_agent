using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using static TallyAgent.Core.Tally.TallyXml;

namespace TallyAgent.Core.Tally.Extractors;

using Row = Dictionary<string, object?>;

/// <summary>Snapshot reports: Trial Balance, Balance Sheet, P&amp;L, Stock Summary,
/// outstanding payables/receivables. Handles both the TallyPrime flat-sibling
/// DSPACCNAME layout and the older nested layout.</summary>
public sealed class ReportExtractor(TallyClient client, ILogger<ReportExtractor> log)
{
    /// <summary>DSPACCNAME → DSPDISPNAME (TallyPrime) or element text (legacy).</summary>
    private static string DspName(XElement el)
    {
        var disp = el.Element("DSPDISPNAME")?.Value;
        return (disp ?? el.Value ?? "").Trim();
    }

    public async Task<List<Row>> TrialBalance(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Report("Trial Balance", from, to, client.Company), ct);
        var rows = WalkNameAmountPairs(doc, (name, amtEl) =>
        {
            var dr = amtEl.Element("DSPCLDR") is not null ? Num(amtEl, "DSPCLDR") : 0;
            var cr = amtEl.Element("DSPCLCR") is not null ? Num(amtEl, "DSPCLCR") : 0;
            if (dr == 0 && cr == 0) dr = Num(amtEl, "BSMAINAMT");
            return new Row
            {
                ["ledger_name"] = name,
                ["closing_debit"] = Math.Abs(dr),
                ["closing_credit"] = Math.Abs(cr),
                ["net_amount"] = dr - cr,
                ["parent_group"] = "",
            };
        });
        log.LogInformation("Trial balance: {N} rows", rows.Count);
        return rows;
    }

    public async Task<List<Row>> BalanceSheet(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Report("Balance Sheet", from, to, client.Company), ct);
        var rows = WalkNameAmountPairs(doc, (name, amtEl) =>
        {
            var amount = Num(amtEl, "BSMAINAMT");
            if (amount == 0) amount = Num(amtEl, "BSSUBAMT");
            return new Row
            {
                ["ledger_name"] = name,
                ["amount"] = amount,
                ["parent_group"] = "",
                ["category"] = amount >= 0 ? "Liabilities" : "Assets",
            };
        });
        log.LogInformation("Balance sheet: {N} rows", rows.Count);
        return rows;
    }

    public async Task<List<Row>> ProfitLoss(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Report("Profit and Loss A/c", from, to, client.Company), ct);
        var rows = WalkNameAmountPairs(doc, (name, amtEl) =>
        {
            var amount = Num(amtEl, "PLAMT");
            if (amount == 0) amount = Num(amtEl, "BSMAINAMT");
            return new Row
            {
                ["ledger_name"] = name,
                ["amount"] = amount,
                ["parent_group"] = "",
            };
        });
        log.LogInformation("P&L: {N} rows", rows.Count);
        return rows;
    }

    public async Task<List<Row>> StockSummary(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Report("Stock Summary", from, to, client.Company), ct);
        var rows = new List<Row>();

        // TallyPrime: DSPACCNAME followed by DSPSTKINFO sibling
        var root = doc.Root;
        if (root is null) return rows;
        var children = root.Elements().ToList();
        for (var i = 0; i < children.Count - 1; i++)
        {
            if (children[i].Name.LocalName != "DSPACCNAME") continue;
            var name = DspName(children[i]);
            var info = children[i + 1];
            if (name.Length == 0) continue;

            var cl = info.Descendants("DSPSTKCL").FirstOrDefault() ?? info;
            rows.Add(new Row
            {
                ["item_name"] = name,
                ["stock_group"] = "",
                ["opening_qty"] = QtyNum(info, "DSPSTKOPQTY", "DSPOPENQTY"),
                ["opening_value"] = Num(info, "DSPSTKOPAMT"),
                ["inward_qty"] = QtyNum(info, "DSPSTKINQTY", "DSPINWQTY"),
                ["inward_value"] = Num(info, "DSPSTKINAMT"),
                ["outward_qty"] = QtyNum(info, "DSPSTKOUTQTY", "DSPOUTQTY"),
                ["outward_value"] = Num(info, "DSPSTKOUTAMT"),
                ["closing_qty"] = QtyNum(cl, "DSPCLQTY", "DSPSTKCLQTY"),
                ["closing_value"] = Num(cl, "DSPCLAMTA") != 0 ? Num(cl, "DSPCLAMTA") : Num(cl, "DSPSTKCLAMT"),
            });
        }
        log.LogInformation("Stock summary: {N} rows", rows.Count);
        return rows;
    }

    /// <summary>Outstanding = closing balances of ledgers under the given parent groups
    /// (Sundry Creditors / Sundry Debtors), derived from the Ledger collection so it
    /// works uniformly across Tally versions.</summary>
    public async Task<List<Row>> Outstanding(string parentGroupContains, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Collection("Ledger", ["NAME", "PARENT", "CLOSINGBALANCE"], client.Company), ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants("LEDGER"))
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
        foreach (var el in doc.Descendants("LEDGER"))
        {
            var parent = Text(el, "PARENT");
            if (parent.Contains("Bank", StringComparison.OrdinalIgnoreCase))
                set.Add(Text(el, "NAME"));
        }
        return set;
    }

    private static double QtyNum(XElement el, params string[] tags)
    {
        foreach (var tag in tags)
        {
            foreach (var d in el.DescendantsAndSelf(tag))
            {
                var v = Num(d.Parent ?? d, tag);
                if (v != 0) return v;
            }
        }
        return 0;
    }

    private List<Row> WalkNameAmountPairs(XDocument doc, Func<string, XElement, Row> mapPair)
    {
        var rows = new List<Row>();
        var root = doc.Root;
        if (root is null) return rows;

        var children = root.Elements().ToList();
        var i = 0;
        while (i < children.Count)
        {
            var el = children[i];
            if (el.Name.LocalName == "DSPACCNAME")
            {
                var name = DspName(el);
                if (name.Length > 0 && i + 1 < children.Count)
                {
                    rows.Add(mapPair(name, children[i + 1]));
                    i += 2;
                    continue;
                }
            }
            else if (el.Name.LocalName is "DSPTOTINFO" or "DSPACCINFO")
            {
                // legacy nested layout
                var nameEl = el.Element("DSPACCNAME");
                if (nameEl is not null)
                {
                    var name = DspName(nameEl);
                    if (name.Length > 0) rows.Add(mapPair(name, el));
                }
            }
            i++;
        }
        return rows;
    }
}
