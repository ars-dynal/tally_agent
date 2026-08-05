using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using static TallyAgent.Core.Tally.TallyXml;

namespace TallyAgent.Core.Tally.Extractors;

using Row = Dictionary<string, object?>;

/// <summary>All voucher-derived datasets produced from one date-bounded voucher
/// collection fetch per window, fanned out in memory.</summary>
public sealed class VoucherExtractor(TallyClient client, ILogger<VoucherExtractor> log)
{
    public sealed class DayBookResult
    {
        public List<Row> Vouchers { get; } = [];
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

    /// <summary>Fetch vouchers for a window and fan out to all voucher datasets.
    /// The explicit collection-level date filter avoids dependence on the period
    /// selected in the interactive Tally UI.</summary>
    public async Task<DayBookResult> ExtractWindow(DateOnly from, DateOnly to,
        ISet<string> bankLedgerNames, CancellationToken ct)
    {
        var doc = await client.PostAsync(
            TallyEnvelopes.VoucherCollection(from, to, client.Company), ct);
        var result = new DayBookResult();
        var seenVoucherKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outOfWindow = 0;
        var invalidDates = 0;
        var duplicateVouchers = 0;

        foreach (var v in doc.Descendants("VOUCHER"))
        {
            var voucherDateText = Date(v, "DATE");
            if (!TryParseIsoDate(voucherDateText, out var voucherDate))
            {
                invalidDates++;
                log.LogWarning(
                    "Skipping voucher {VoucherNumber}: missing or invalid DATE '{RawDate}' for requested window {From}..{To}",
                    Text(v, "VOUCHERNUMBER"), Text(v, "DATE"), from, to);
                continue;
            }

            if (voucherDate < from || voucherDate > to)
            {
                outOfWindow++;
                log.LogWarning(
                    "Skipping out-of-window voucher {VoucherNumber} dated {VoucherDate}; requested {From}..{To}",
                    Text(v, "VOUCHERNUMBER"), voucherDate, from, to);
                continue;
            }

            var guid = Text(v, "GUID");
            var vchType = Text(v, "VOUCHERTYPENAME");
            var voucherNumber = Text(v, "VOUCHERNUMBER");
            var voucherKey = guid.Length > 0
                ? guid
                : $"{voucherDate:yyyy-MM-dd}|{vchType}|{voucherNumber}|{Text(v, "MASTERID")}";

            if (!seenVoucherKeys.Add(voucherKey))
            {
                duplicateVouchers++;
                log.LogWarning(
                    "Skipping duplicate voucher {VoucherNumber} ({VoucherKey}) in window {From}..{To}",
                    voucherNumber, voucherKey, from, to);
                continue;
            }

            var header = new Row
            {
                ["voucher_date"] = voucherDateText,
                ["voucher_type"] = vchType,
                ["voucher_number"] = voucherNumber,
                ["reference"] = Text(v, "REFERENCE"),
                ["narration"] = Text(v, "NARRATION"),
                ["party_name"] = Text(v, "PARTYLEDGERNAME"),
                ["guid"] = guid,
                ["master_id"] = Int(v, "MASTERID"),
                ["alter_id"] = Int(v, "ALTERID"),
                ["is_cancelled"] = Bool(v, "ISCANCELLED"),
                ["amount"] = Num(v, "AMOUNT"),
            };
            result.VoucherHeaders.Add(header);

            var ledgerEntries = v.Descendants("ALLLEDGERENTRIES.LIST")
                .Concat(v.Descendants("LEDGERENTRIES.LIST"))
                .Distinct()
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
                    ["voucher_date"] = voucherDateText,
                    ["voucher_number"] = voucherNumber,
                    ["ledger_name"] = ledgerName,
                    ["amount"] = amount,
                    ["is_deemed_positive"] = deemedPositive,
                });

                var flat = new Row
                {
                    ["voucher_date"] = voucherDateText,
                    ["voucher_type"] = vchType,
                    ["voucher_number"] = voucherNumber,
                    ["reference"] = header["reference"],
                    ["narration"] = header["narration"],
                    ["party_name"] = header["party_name"],
                    ["guid"] = guid,
                    ["master_id"] = header["master_id"],
                    ["alter_id"] = header["alter_id"],
                    ["is_cancelled"] = header["is_cancelled"],
                    ["is_optional"] = Bool(v, "ISOPTIONAL"),
                    ["ledger_name"] = ledgerName,
                    ["amount"] = amount,
                    ["is_deemed_positive"] = deemedPositive,
                };
                result.Vouchers.Add(flat);
                result.DayBook.Add(new Row(flat));

                var upper = ledgerName.ToUpperInvariant();
                if (upper.Contains("CGST")) cgst += Math.Abs(amount);
                else if (upper.Contains("SGST") || upper.Contains("UTGST")) sgst += Math.Abs(amount);
                else if (upper.Contains("IGST")) igst += Math.Abs(amount);

                if (bankLedgerNames.Contains(ledgerName))
                {
                    var bank = entry.Descendants("BANKALLOCATIONS.LIST").FirstOrDefault();
                    result.BankBook.Add(new Row
                    {
                        ["bank_account"] = ledgerName,
                        ["txn_date"] = voucherDateText,
                        ["voucher_type"] = vchType,
                        ["voucher_number"] = voucherNumber,
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
                        ["voucher_date"] = voucherDateText,
                        ["voucher_number"] = voucherNumber,
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
                        ["voucher_date"] = voucherDateText,
                        ["voucher_number"] = voucherNumber,
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

                foreach (var cc in entry.Elements("COSTCENTREALLOCATIONS.LIST"))
                {
                    var ccName = Text(cc, "NAME");
                    if (ccName.Length == 0) continue;
                    result.CostCentreAllocations.Add(new Row
                    {
                        ["voucher_guid"] = guid,
                        ["voucher_date"] = voucherDateText,
                        ["voucher_type"] = vchType,
                        ["ledger_name"] = ledgerName,
                        ["cost_centre"] = ccName,
                        ["cost_category"] = "",
                        ["amount"] = Num(cc, "AMOUNT"),
                    });
                }

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
                            ["voucher_date"] = voucherDateText,
                            ["voucher_type"] = vchType,
                            ["ledger_name"] = ledgerName,
                            ["cost_centre"] = ccName,
                            ["cost_category"] = catName,
                            ["amount"] = Num(cc, "AMOUNT"),
                        });
                    }
                }
            }

            foreach (var inv in v.Descendants("INVENTORYENTRIES.LIST")
                         .Concat(v.Descendants("ALLINVENTORYENTRIES.LIST"))
                         .Distinct())
            {
                var stockItem = Text(inv, "STOCKITEMNAME");
                if (stockItem.Length == 0) continue;
                var invRow = new Row
                {
                    ["voucher_guid"] = guid,
                    ["voucher_date"] = voucherDateText,
                    ["voucher_number"] = voucherNumber,
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
                        ["invoice_number"] = voucherNumber,
                        ["invoice_date"] = voucherDateText,
                        ["stock_item"] = stockItem,
                        ["quantity"] = invRow["quantity"],
                        ["rate"] = invRow["rate"],
                        ["amount"] = invRow["amount"],
                        ["godown"] = invRow["godown"],
                    });
                }
            }

            if (IsSalesType(vchType))
                result.SalesRegister.Add(RegisterRow(header, vchType, cgst, sgst, igst));
            else if (IsPurchaseType(vchType))
                result.PurchaseRegister.Add(RegisterRow(header, vchType, cgst, sgst, igst));
        }

        if (outOfWindow > 0 || invalidDates > 0 || duplicateVouchers > 0)
        {
            log.LogWarning(
                "Voucher window {From}..{To} rejected {OutOfWindow} out-of-window, {InvalidDates} invalid-date and {Duplicates} duplicate vouchers",
                from, to, outOfWindow, invalidDates, duplicateVouchers);
        }

        log.LogInformation(
            "Voucher window {From}..{To}: {V} vouchers, {L} lines, {B} bills, {BA} bank, {CC} cost-centre, {I} inventory",
            from, to, result.VoucherHeaders.Count, result.VoucherLines.Count,
            result.BillAllocations.Count, result.BankAllocations.Count,
            result.CostCentreAllocations.Count, result.InventoryEntries.Count);
        return result;
    }

    private static bool TryParseIsoDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

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
        ["master_id"] = header["master_id"],
        ["alter_id"] = header["alter_id"],
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
