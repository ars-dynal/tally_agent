using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using static TallyAgent.Core.Tally.TallyXml;

namespace TallyAgent.Core.Tally.Extractors;

using Row = Dictionary<string, object?>;

/// <summary>All voucher-derived datasets produced from ONE Day Book fetch per
/// date window, fanned out in memory. Covers every voucher type (sales, purchase,
/// receipt, payment, journal, contra, notes, stock journals, orders, physical
/// stock, ...) because Day Book returns them all tagged with VOUCHERTYPENAME.</summary>
public sealed class VoucherExtractor(TallyClient client, ILogger<VoucherExtractor> log)
{
    public sealed class DayBookResult
    {
        public List<Row> Vouchers { get; } = [];            // flat: header × ledger line
        public List<Row> VoucherHeaders { get; } = [];
        public List<Row> VoucherLines { get; } = [];
        public List<Row> BillAllocations { get; } = [];
        public List<Row> BankAllocations { get; } = [];
        public List<Row> CostCentreAllocations { get; } = [];
        public List<Row> InventoryEntries { get; } = [];
        public List<Row> SalesRegister { get; } = [];
        public List<Row> PurchaseRegister { get; } = [];
        public List<Row> SalesInvoiceLines { get; } = [];
        public List<Row> DayBook { get; } = [];
        public List<Row> BankBook { get; } = [];
    }

    /// <summary>Fetch Day Book for a window and fan out to all voucher datasets.
    /// bankLedgerNames drives the bank_book dataset (ledgers under Bank Accounts / Bank OD).</summary>
    public async Task<DayBookResult> ExtractWindow(DateOnly from, DateOnly to,
        ISet<string> bankLedgerNames, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.Report("Day Book", from, to, client.Company), ct);
        var result = new DayBookResult();

        foreach (var v in doc.Descendants("VOUCHER"))
        {
            var guid = Text(v, "GUID");
            var vchType = Text(v, "VOUCHERTYPENAME");
            var header = new Row
            {
                ["voucher_date"] = Date(v, "DATE"),
                ["voucher_type"] = vchType,
                ["voucher_number"] = Text(v, "VOUCHERNUMBER"),
                ["reference"] = Text(v, "REFERENCE"),
                ["narration"] = Text(v, "NARRATION"),
                ["party_name"] = Text(v, "PARTYLEDGERNAME"),
                ["guid"] = guid,
                ["is_cancelled"] = Bool(v, "ISCANCELLED"),
                ["amount"] = Num(v, "AMOUNT"),
            };
            result.VoucherHeaders.Add(header);

            var ledgerEntries = v.Descendants("ALLLEDGERENTRIES.LIST")
                .Concat(v.Descendants("LEDGERENTRIES.LIST"))
                .ToList();

            double cgst = 0, sgst = 0, igst = 0;

            foreach (var entry in ledgerEntries)
            {
                var ledgerName = Text(entry, "LEDGERNAME");
                var amount = Num(entry, "AMOUNT");
                var deemedPositive = Bool(entry, "ISDEEMEDPOSITIVE");

                result.VoucherLines.Add(new Row
                {
                    ["voucher_guid"] = guid,
                    ["ledger_name"] = ledgerName,
                    ["amount"] = amount,
                    ["is_deemed_positive"] = deemedPositive,
                });

                // Flat vouchers + day_book: header columns × line columns
                var flat = new Row
                {
                    ["voucher_date"] = header["voucher_date"],
                    ["voucher_type"] = vchType,
                    ["voucher_number"] = header["voucher_number"],
                    ["reference"] = header["reference"],
                    ["narration"] = header["narration"],
                    ["party_name"] = header["party_name"],
                    ["guid"] = guid,
                    ["is_cancelled"] = header["is_cancelled"],
                    ["is_optional"] = Bool(v, "ISOPTIONAL"),
                    ["ledger_name"] = ledgerName,
                    ["amount"] = amount,
                    ["is_deemed_positive"] = deemedPositive,
                };
                result.Vouchers.Add(flat);
                result.DayBook.Add(new Row(flat));

                // GST split for registers
                var upper = ledgerName.ToUpperInvariant();
                if (upper.Contains("CGST")) cgst += Math.Abs(amount);
                else if (upper.Contains("SGST") || upper.Contains("UTGST")) sgst += Math.Abs(amount);
                else if (upper.Contains("IGST")) igst += Math.Abs(amount);

                // Bank book rows for bank ledgers
                if (bankLedgerNames.Contains(ledgerName))
                {
                    var bank = entry.Descendants("BANKALLOCATIONS.LIST").FirstOrDefault();
                    result.BankBook.Add(new Row
                    {
                        ["bank_account"] = ledgerName,
                        ["txn_date"] = header["voucher_date"],
                        ["voucher_type"] = vchType,
                        ["voucher_number"] = header["voucher_number"],
                        ["particulars"] = header["party_name"] is string p && p.Length > 0
                            ? p : header["narration"],
                        ["debit"] = amount > 0 ? Math.Abs(amount) : 0.0,
                        ["credit"] = amount < 0 ? Math.Abs(amount) : 0.0,
                        ["narration"] = header["narration"],
                        ["cheque_number"] = bank is null ? "" : Text(bank, "INSTRUMENTNUMBER"),
                        ["instrument_date"] = bank is null ? null : Date(bank, "INSTRUMENTDATE"),
                    });
                }

                foreach (var ba in entry.Descendants("BILLALLOCATIONS.LIST"))
                {
                    result.BillAllocations.Add(new Row
                    {
                        ["voucher_guid"] = guid,
                        ["ledger_name"] = ledgerName,
                        ["bill_ref"] = Text(ba, "NAME"),
                        ["bill_type"] = Text(ba, "BILLTYPE"),
                        ["amount"] = Num(ba, "AMOUNT"),
                    });
                }

                foreach (var bk in entry.Descendants("BANKALLOCATIONS.LIST"))
                {
                    result.BankAllocations.Add(new Row
                    {
                        ["voucher_guid"] = guid,
                        ["voucher_date"] = header["voucher_date"],
                        ["voucher_number"] = header["voucher_number"],
                        ["voucher_type"] = vchType,
                        ["ledger_name"] = ledgerName,
                        ["transaction_type"] = Text(bk, "TRANSACTIONTYPE"),
                        ["instrument_date"] = Date(bk, "INSTRUMENTDATE"),
                        ["instrument_number"] = Text(bk, "INSTRUMENTNUMBER"),
                        ["bank_name"] = Text(bk, "BANKNAME"),
                        ["amount"] = Num(bk, "AMOUNT"),
                        ["bankers_date"] = Date(bk, "BANKERSDATE"),
                    });
                }

                // Direct cost-centre allocations
                foreach (var cc in entry.Elements("COSTCENTREALLOCATIONS.LIST"))
                {
                    var ccName = Text(cc, "NAME");
                    if (ccName.Length == 0) continue;
                    result.CostCentreAllocations.Add(new Row
                    {
                        ["voucher_guid"] = guid,
                        ["voucher_date"] = header["voucher_date"],
                        ["voucher_type"] = vchType,
                        ["ledger_name"] = ledgerName,
                        ["cost_centre"] = ccName,
                        ["cost_category"] = "",
                        ["amount"] = Num(cc, "AMOUNT"),
                    });
                }
                // Category-nested cost-centre allocations
                foreach (var cat in entry.Descendants("CATEGORYALLOCATIONS.LIST"))
                {
                    var catName = Text(cat, "CATEGORY");
                    foreach (var cc in cat.Descendants("COSTCENTREALLOCATIONS.LIST"))
                    {
                        var ccName = Text(cc, "NAME");
                        if (ccName.Length == 0) continue;
                        result.CostCentreAllocations.Add(new Row
                        {
                            ["voucher_guid"] = guid,
                            ["voucher_date"] = header["voucher_date"],
                            ["voucher_type"] = vchType,
                            ["ledger_name"] = ledgerName,
                            ["cost_centre"] = ccName,
                            ["cost_category"] = catName,
                            ["amount"] = Num(cc, "AMOUNT"),
                        });
                    }
                }
            }

            // Inventory entries
            foreach (var inv in v.Descendants("INVENTORYENTRIES.LIST")
                         .Concat(v.Descendants("ALLINVENTORYENTRIES.LIST")))
            {
                var stockItem = Text(inv, "STOCKITEMNAME");
                if (stockItem.Length == 0) continue;
                var invRow = new Row
                {
                    ["voucher_guid"] = guid,
                    ["stock_item"] = stockItem,
                    ["quantity"] = Num(inv, "ACTUALQTY"),
                    ["rate"] = Num(inv, "RATE"),
                    ["amount"] = Num(inv, "AMOUNT"),
                    ["godown"] = FirstGodown(inv),
                };
                result.InventoryEntries.Add(invRow);

                if (IsSalesType(vchType))
                {
                    result.SalesInvoiceLines.Add(new Row
                    {
                        ["voucher_guid"] = guid,
                        ["invoice_number"] = header["voucher_number"],
                        ["stock_item"] = stockItem,
                        ["quantity"] = invRow["quantity"],
                        ["rate"] = invRow["rate"],
                        ["amount"] = invRow["amount"],
                        ["godown"] = invRow["godown"],
                    });
                }
            }

            // Sales / purchase registers with GST breakup
            if (IsSalesType(vchType))
                result.SalesRegister.Add(RegisterRow(header, vchType, cgst, sgst, igst));
            else if (IsPurchaseType(vchType))
                result.PurchaseRegister.Add(RegisterRow(header, vchType, cgst, sgst, igst));
        }

        log.LogInformation(
            "Day Book {From}..{To}: {V} vouchers, {L} lines, {B} bills, {BA} bank, {CC} cost-centre, {I} inventory",
            from, to, result.VoucherHeaders.Count, result.VoucherLines.Count,
            result.BillAllocations.Count, result.BankAllocations.Count,
            result.CostCentreAllocations.Count, result.InventoryEntries.Count);
        return result;
    }

    private static string FirstGodown(XElement inv)
    {
        foreach (var alloc in inv.Descendants("BATCHALLOCATIONS.LIST"))
        {
            var g = Text(alloc, "GODOWNNAME");
            if (g.Length > 0) return g;
        }
        return Text(inv, "GODOWNNAME");
    }

    private static Row RegisterRow(Row header, string vchType, double cgst, double sgst, double igst) => new()
    {
        ["invoice_date"] = header["voucher_date"],
        ["invoice_number"] = header["voucher_number"],
        ["voucher_type"] = vchType,
        ["party_name"] = header["party_name"],
        ["narration"] = header["narration"],
        ["reference"] = header["reference"],
        ["guid"] = header["guid"],
        ["total_amount"] = Math.Abs((double)header["amount"]!),
        ["cgst"] = cgst,
        ["sgst"] = sgst,
        ["igst"] = igst,
    };

    private static bool IsSalesType(string t) =>
        t.Contains("sales", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("credit note", StringComparison.OrdinalIgnoreCase);

    private static bool IsPurchaseType(string t) =>
        t.Contains("purchase", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("debit note", StringComparison.OrdinalIgnoreCase);
}
