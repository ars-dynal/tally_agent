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
        /// <summary>Slim (guid, date, type, alter_id, is_cancelled) manifest per
        /// window — the warehouse diffs it against stored GUIDs to detect
        /// vouchers DELETED in Tally (they simply vanish from extraction and can
        /// only be found by comparison). See ARCHITECTURE §8.2.</summary>
        public List<Row> Manifest { get; } = [];
        /// <summary>Actual voucher-date extent ACCEPTED in this window (coverage evidence).</summary>
        public string? MinVoucherDate { get; set; }
        public string? MaxVoucherDate { get; set; }

        /// <summary>Vouchers Tally returned whose date is outside the window we
        /// asked for. Tally bounds exports by the ACTIVE period (Alt+F2) and
        /// ignores SVFROMDATE/SVTODATE when they fall outside it, so a non-zero
        /// count here is direct evidence of what period Tally is really serving.</summary>
        public int OutOfWindowCount { get; set; }

        /// <summary>Date extent of EVERY voucher Tally returned, accepted or
        /// not. When it does not line up with the requested window, this is the
        /// range Tally actually served — the only observable signal for the
        /// active period, which Tally does not expose over XML.</summary>
        public string? ServedMinDate { get; set; }
        public string? ServedMaxDate { get; set; }
    }

    /// <summary>
    /// Fetch vouchers for a window and fan out to all voucher datasets.
    ///
    /// ONE REQUEST PER DAY, driven by SVCURRENTDATE.
    ///
    /// Measured 2026-09-04: the Day Book report ignores SVFROMDATE and SVTODATE
    /// completely and reports whatever day SVCURRENTDATE names. Asked for
    /// 5-Apr..7-Apr with SVCURRENTDATE=7-Apr it returned 85 vouchers, all dated
    /// 7-Apr. A range request is therefore not a smaller request — it is the
    /// wrong request, and the earlier stall was the agent believing otherwise.
    ///
    /// So the window is walked a day at a time. Each request is tiny and bounded
    /// (12.6 MB for the heaviest day observed, ~148 KB per voucher, against a
    /// 256 MB cap), empty days come back empty and cost nothing, and nothing is
    /// discarded client-side. About 2,900 requests cover 2019-2027.
    ///
    /// The window still exists as the CHECKPOINT unit: a 7-day window is 7
    /// requests and one enqueue, so a resumed walk restarts at most a week of
    /// cheap requests rather than re-running a month.
    /// </summary>
    public async Task<DayBookResult> ExtractWindow(DateOnly from, DateOnly to,
        ISet<string> bankLedgerNames, CancellationToken ct)
    {
        var result = new DayBookResult();
        var seenVoucherKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outOfWindow = 0;
        var invalidDates = 0;
        var duplicateVouchers = 0;

        for (var day = from; day <= to; day = day.AddDays(1))
        {
        ct.ThrowIfCancellationRequested();
        // Single-day requests cannot be split, so they keep the full retry
        // ladder; there is no larger size left to fall back to.
        var doc = await client.PostAsync(
            TallyEnvelopes.Report("Day Book", day, day, client.Company, currentDate: day),
            requestTimeout: client.VoucherRequestTimeout,
            maxTimeoutRetries: null, ct);

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

            // Record the served extent BEFORE the window test: a voucher we are
            // about to reject is exactly the evidence we need.
            if (result.ServedMinDate is null ||
                string.CompareOrdinal(voucherDateText, result.ServedMinDate) < 0)
                result.ServedMinDate = voucherDateText;
            if (result.ServedMaxDate is null ||
                string.CompareOrdinal(voucherDateText, result.ServedMaxDate) > 0)
                result.ServedMaxDate = voucherDateText;

            // EQUALITY, not a range. One request asks for one day, so every
            // voucher in the response must carry that date. This is a stronger
            // check than the range test it replaces: each of the ~2,900 requests
            // is individually verifiable instead of a month being spot-checked.
            if (voucherDate != day)
            {
                outOfWindow++;
                log.LogWarning(
                    "Skipping voucher {VoucherNumber} dated {VoucherDate}; requested exactly {Day}",
                    Text(v, "VOUCHERNUMBER"), voucherDate, day);
                continue;
            }

            var guid = Text(v, "GUID");
            // Day Book emits VCHTYPE as an ATTRIBUTE on <VOUCHER>; the
            // collection emitted VOUCHERTYPENAME as a child. Text() checks
            // children then attributes, so both shapes read the same way.
            var vchType = Text(v, "VOUCHERTYPENAME");
            if (vchType.Length == 0) vchType = Text(v, "VCHTYPE");
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

            var isCancelled = Bool(v, "ISCANCELLED");
            var isOptional = Bool(v, "ISOPTIONAL");
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
                ["is_cancelled"] = isCancelled,
                ["is_optional"] = isOptional,
                // Lifecycle contract (§8.1): is_deleted flips to true only via
                // GUID-manifest reconciliation in the warehouse — the agent always
                // emits false because a voucher it can see is, by definition, not
                // deleted. source_last_seen_at is audit-like (excluded from the
                // content checksum by BatchBuilder).
                ["is_deleted"] = false,
                ["source_status"] = isCancelled ? "cancelled" : isOptional ? "optional" : "active",
                ["source_last_seen_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["amount"] = Num(v, "AMOUNT"),
            };
            result.VoucherHeaders.Add(header);
            if (result.MinVoucherDate is null ||
                string.CompareOrdinal(voucherDateText, result.MinVoucherDate) < 0)
                result.MinVoucherDate = voucherDateText;
            if (result.MaxVoucherDate is null ||
                string.CompareOrdinal(voucherDateText, result.MaxVoucherDate) > 0)
                result.MaxVoucherDate = voucherDateText;
            result.Manifest.Add(new Row
            {
                ["guid"] = guid,
                ["voucher_date"] = voucherDateText,
                ["voucher_type"] = vchType,
                ["alter_id"] = header["alter_id"],
                ["is_cancelled"] = isCancelled,
            });

            // Tally may expose ledger lines as ALLLEDGERENTRIES.LIST (voucher view)
            // and/or LEDGERENTRIES.LIST (invoice view) — the SAME lines in two
            // shapes. Concat+Distinct was reference-equality (a no-op) and doubled
            // every line on builds returning both. Prefer ALL*; fall back only
            // when it is absent.
            var allLedger = v.Descendants("ALLLEDGERENTRIES.LIST").ToList();
            var ledgerEntries = allLedger.Count > 0
                ? allLedger
                : v.Descendants("LEDGERENTRIES.LIST").ToList();

            double cgst = 0, sgst = 0, igst = 0;
            // Per-voucher, per-entry-type ordinals. NOTE (child identity contract):
            // these ordinals identify a line only WITHIN one extraction of one
            // voucher — they are NOT stable across edits. The warehouse must
            // replace the entire child set per (source_company_id, voucher_guid,
            // entry_type) on every new version of a voucher, never merge by ordinal.
            int lineIndex = 0, billIndex = 0, bankIndex = 0, ccIndex = 0, invIndex = 0;

            foreach (var entry in ledgerEntries)
            {
                var ledgerName = Text(entry, "LEDGERNAME");
                var amount = Num(entry, "AMOUNT");
                var deemedPositive = Bool(entry, "ISDEEMEDPOSITIVE");

                result.VoucherLines.Add(new Row
                {
                    ["voucher_guid"] = guid,
                    ["entry_type"] = "ledger",
                    ["line_index"] = lineIndex,
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
                    ["is_optional"] = isOptional,
                    ["is_deleted"] = false,
                    ["source_status"] = header["source_status"],
                    ["source_last_seen_at"] = header["source_last_seen_at"],
                    ["line_index"] = lineIndex,
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

                // These rows ARE the outstandings. Matching New Ref / Advance
                // against Agst Ref by bill_ref within a party reproduces Bills
                // Payable and Bills Receivable in SQL, which is why the agent no
                // longer asks Tally to compute those reports at all.
                // ledger_name is the PARTY ledger: bill allocations hang off the
                // party's ledger entry, never off the expense or stock lines.
                foreach (var ba in entry.Descendants("BILLALLOCATIONS.LIST"))
                {
                    result.BillAllocations.Add(new Row
                    {
                        ["voucher_guid"] = guid,
                        ["entry_type"] = "bill_allocation",
                        ["line_index"] = billIndex++,
                        ["voucher_date"] = voucherDateText,
                        ["voucher_number"] = voucherNumber,
                        // Was missing until v2.3.0 while every other allocation
                        // row carried it. Without it the SQL cannot tell a
                        // purchase from a payment.
                        ["voucher_type"] = vchType,
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
                        ["entry_type"] = "bank_allocation",
                        ["line_index"] = bankIndex++,
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
                        ["entry_type"] = "cost_centre_allocation",
                        ["line_index"] = ccIndex++,
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
                            ["entry_type"] = "cost_centre_allocation",
                            ["line_index"] = ccIndex++,
                            ["voucher_date"] = voucherDateText,
                            ["voucher_type"] = vchType,
                            ["ledger_name"] = ledgerName,
                            ["cost_centre"] = ccName,
                            ["cost_category"] = catName,
                            ["amount"] = Num(cc, "AMOUNT"),
                        });
                    }
                }

                lineIndex++;
            }

            // Same dual-shape issue as ledger entries: prefer ALLINVENTORYENTRIES,
            // fall back to INVENTORYENTRIES only when it is absent (Concat+Distinct
            // was reference-equality and double-counted stock movements).
            var allInventory = v.Descendants("ALLINVENTORYENTRIES.LIST").ToList();
            var inventoryEntries = allInventory.Count > 0
                ? allInventory
                : v.Descendants("INVENTORYENTRIES.LIST").ToList();

            foreach (var inv in inventoryEntries)
            {
                var stockItem = Text(inv, "STOCKITEMNAME");
                if (stockItem.Length == 0) continue;
                var invRow = new Row
                {
                    ["voucher_guid"] = guid,
                    ["entry_type"] = "inventory",
                    ["line_index"] = invIndex++,
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
        }   // day

        if (outOfWindow > 0 || invalidDates > 0 || duplicateVouchers > 0)
        {
            log.LogWarning(
                "Voucher window {From}..{To} rejected {OutOfWindow} wrong-date, {InvalidDates} invalid-date and {Duplicates} duplicate vouchers",
                from, to, outOfWindow, invalidDates, duplicateVouchers);
        }

        log.LogInformation(
            "Voucher window {From}..{To} ({Days} daily requests): {V} vouchers, {L} lines, {B} bills, {BA} bank, {CC} cost-centre, {I} inventory",
            from, to, to.DayNumber - from.DayNumber + 1,
            result.VoucherHeaders.Count, result.VoucherLines.Count,
            result.BillAllocations.Count, result.BankAllocations.Count,
            result.CostCentreAllocations.Count, result.InventoryEntries.Count);
        result.OutOfWindowCount = outOfWindow;
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
