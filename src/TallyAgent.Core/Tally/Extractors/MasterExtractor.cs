using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TallyAgent.Core.Data;
using static TallyAgent.Core.Tally.TallyXml;

namespace TallyAgent.Core.Tally.Extractors;

using Row = Dictionary<string, object?>;

/// <summary>Extracts the 13 master/inventory-master collections via TDL FETCH lists.
/// Field lists and fallback chains ported from the proven Python connector.
///
/// ONE FETCH PER COLLECTION PER CYCLE (v2.0.5): v2.0.4 asked Tally for the
/// Ledger collection five times and the StockItem collection four times every
/// cycle (ledgers, opening_bills, outstanding ×2, bank names; stock_items,
/// gst_rates, standard costs, standard prices). Each was a full serialization
/// inside Tally's UI thread. The Ledger and StockItem documents are now
/// fetched once with the union of fields and cached for the cycle
/// (<see cref="BeginCycle"/> / <see cref="EndCycle"/>); the dependent
/// datasets are derived in memory.
///
/// COMPUTED BALANCES ONCE A DAY: OPENING/CLOSINGBALANCE on ledgers and
/// CLOSINGBALANCE/VALUE/RATE on stock items force Tally to walk every voucher
/// for every master (a full valuation) on each master export. They are
/// therefore requested from Tally only when the engine says so (the daily
/// snapshot slot, first run, Force Full Sync, or every cycle when
/// tally.includeMasterBalances=true) and persisted per GUID in SQLite; every
/// other master export fills the balance columns from that store, so the
/// ledgers / stock_items datasets ALWAYS carry balances (as of the last daily
/// capture) without re-valuing the company each cycle.</summary>
public sealed class MasterExtractor(TallyClient client, MasterBalanceRepository balanceStore,
    ILogger<MasterExtractor> log)
{
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private XDocument? _ledgerDoc;
    private XDocument? _stockItemDoc;
    private bool _fetchBalances;
    private string _company = "";
    private DateOnly _asOf = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Start a cycle. <paramref name="fetchBalances"/>: ask Tally for
    /// computed balances this cycle (and persist them); otherwise balances are
    /// filled from the last capture. <paramref name="asOf"/>: the date the
    /// balances are computed AS OF (see <see cref="BalanceBearingCollection"/>);
    /// defaults to today.</summary>
    public void BeginCycle(string company, bool fetchBalances, DateOnly? asOf = null)
    {
        EndCycle();
        _company = company;
        _fetchBalances = fetchBalances || client.IncludeMasterBalances;
        _asOf = asOf ?? DateOnly.FromDateTime(DateTime.Today);
    }
    public void EndCycle() { _ledgerDoc = null; _stockItemDoc = null; }

    /// <summary>True when this cycle captured fresh balances from Tally.</summary>
    public bool FetchedBalancesThisCycle => _fetchBalances;

    /// <summary>The date balance-bearing collections are computed as of.</summary>
    public DateOnly AsOfDate => _asOf;

    private async Task<XDocument> FetchCollection(string type, string[] fields, CancellationToken ct) =>
        await client.PostAsync(TallyEnvelopes.Collection(type, fields, client.Company), ct);

    /// <summary>
    /// A collection whose values are COMPUTED as of a date — Ledger
    /// (OPENING/CLOSINGBALANCE) and StockItem (CLOSINGBALANCE/VALUE/RATE).
    ///
    /// SVTODATE is pinned. Without it Tally evaluates $ClosingBalance against
    /// whatever period is currently loaded, so the as-of date of every master
    /// balance was decided by whoever last pressed Alt+F2 rather than by the
    /// agent. Observed 2026-09: 10 of 69 Indirect Expense ledgers stale, Salary
    /// and Wages tying exactly at 31-Jul — a period end, not a drift, and only
    /// the ledgers with August activity differed.
    ///
    /// ReportExtractor has always pinned it on this same Ledger collection for
    /// the outstandings and the trial-balance fallback; this makes the master
    /// export agree with it instead of contradicting it.
    ///
    /// Masters are not period-bound, so this changes WHEN the balances are
    /// measured, not WHICH masters come back.
    /// </summary>
    private async Task<XDocument> BalanceBearingCollection(string type, string[] fields, CancellationToken ct) =>
        await client.PostAsync(
            TallyEnvelopes.Collection(type, fields, client.Company, from: null, to: _asOf), ct);

    private static readonly string[] LedgerBaseFields =
    [
        "GUID","MASTERID","ALTERID",
        "NAME","PARENT","PARTYGSTIN","GSTIN",
        "GSTREGISTRATIONNUMBER","INCOMETAXNUMBER","LEDSTATENAME","COUNTRYNAME","ADDRESS",
        "PINCODE","LEDGERMOBILE","LEDGERPHONE","EMAIL","LEDGERCONTACT","BANKACCOUNTNUMBER",
        "IFSCODE","BANKNAME","BRANCHNAME","ISBILLWISEON","ISCOSTCENTRESON",
        // No BILLALLOCATIONS here. opening_bills is retired (see
        // DatasetRegistry.RetiredOpeningBills) and nothing else reads them, so
        // asking for them would make Tally serialise a sub-object per ledger
        // that no dataset consumes — the same waste v2.0.5 removed elsewhere.
        // `TallyAgent.Cli diagnose-opening-bills` sends its own field list.
    ];
    private static readonly string[] LedgerBalanceFields = ["OPENINGBALANCE","CLOSINGBALANCE"];

    private static readonly string[] StockItemBaseFields =
    [
        "GUID","MASTERID","ALTERID",
        "NAME","PARENT","CATEGORY","BASEUNITS","OPENINGBALANCE","OPENINGVALUE","OPENINGRATE",
        "GSTRATE","HSNCODE","DESCRIPTION","ADDITIONALNAME",
        "GSTAPPLICABLE","GSTTYPEOFSUPPLY","TAXCLASSIFICATIONNAME","GSTDETAILS.LIST",
        "STANDARDCOSTLIST.LIST","STANDARDPRICELIST.LIST",
    ];
    private static readonly string[] StockItemBalanceFields = ["CLOSINGBALANCE","CLOSINGVALUE","CLOSINGRATE"];

    /// <summary>The Ledger collection for this cycle (fetched once).</summary>
    public async Task<XDocument> LedgerDocument(CancellationToken ct)
    {
        if (_ledgerDoc is { } cached) return cached;
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_ledgerDoc is { } c2) return c2;
            var fields = _fetchBalances
                ? LedgerBaseFields.Concat(LedgerBalanceFields).ToArray()
                : LedgerBaseFields;
            _ledgerDoc = await BalanceBearingCollection("Ledger", fields, ct);
            return _ledgerDoc;
        }
        finally { _cacheLock.Release(); }
    }

    /// <summary>The StockItem collection for this cycle (fetched once).</summary>
    public async Task<XDocument> StockItemDocument(CancellationToken ct)
    {
        if (_stockItemDoc is { } cached) return cached;
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_stockItemDoc is { } c2) return c2;
            var fields = _fetchBalances
                ? StockItemBaseFields.Concat(StockItemBalanceFields).ToArray()
                : StockItemBaseFields;
            _stockItemDoc = await BalanceBearingCollection("StockItem", fields, ct);
            return _stockItemDoc;
        }
        finally { _cacheLock.Release(); }
    }

    /// <summary>Balance columns: fresh from Tally when fetching this cycle
    /// (recorded for persistence), otherwise from the last capture by GUID.</summary>
    private sealed class BalanceSource
    {
        private readonly bool _fresh;
        private readonly Dictionary<string, Dictionary<string, double>> _cached;
        public readonly Dictionary<string, Dictionary<string, double>> Captured = new(StringComparer.Ordinal);
        /// <summary>UTC instant the balance columns are "as of" — now when fresh
        /// from Tally, the last capture time when served from the store, null
        /// when no capture exists yet. Emitted on every record as
        /// <c>balance_as_of</c> so the warehouse never mistakes a cached balance
        /// for one computed at extraction time.</summary>
        public readonly string? AsOf;
        public BalanceSource(bool fresh, Dictionary<string, Dictionary<string, double>> cached, string? asOf)
        { _fresh = fresh; _cached = cached; AsOf = asOf; }
        public object? Get(XElement el, string guid, string tag)
        {
            if (_fresh)
            {
                var v = Num(el, tag);
                if (guid.Length > 0)
                {
                    if (!Captured.TryGetValue(guid, out var d)) Captured[guid] = d = new();
                    d[tag] = v;
                }
                return v;
            }
            return _cached.TryGetValue(guid, out var c) && c.TryGetValue(tag, out var cv) ? cv : null;
        }
    }

    private BalanceSource NewBalanceSource(string dataset)
    {
        if (_fetchBalances)
            // Still the capture instant, NOT the as-of date. With SVTODATE
            // pinned to today the two coincide, and the cached path serves
            // LastCapturedUtc — making this date-only would mix formats in one
            // column. Carrying the as-of date properly is a schema change and a
            // separate decision.
            return new BalanceSource(true, new(), DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        try
        {
            return new BalanceSource(false, balanceStore.Load(dataset, _company),
                balanceStore.LastCapturedUtc(dataset, _company));
        }
        catch (Exception ex)
        {
            log.LogWarning("Master balance cache unavailable for {Dataset} ({Msg}) — balances null this cycle", dataset, ex.Message);
            return new BalanceSource(false, new(), null);
        }
    }

    private void PersistBalances(string dataset, BalanceSource src)
    {
        if (!_fetchBalances || src.Captured.Count == 0) return;
        try { balanceStore.Save(dataset, _company, src.Captured); }
        catch (Exception ex) { log.LogWarning("Could not persist {Dataset} balances ({Msg})", dataset, ex.Message); }
    }

    public async Task<List<Row>> Companies(CancellationToken ct)
    {
        var doc = await FetchCollection("Company", [
            "NAME","BASICCURRENCYNAME","BOOKSFROM","STARTINGFROM","ENDINGAT","EMAIL",
            "WEBSITE","ADDRESS","STATENAME","PINCODE","INCOMETAXNUMBER","GSTREGISTRATIONNUMBER"], ct);
        var rows = doc.Descendants("COMPANY").Select(el => new Row
        {
            ["company_name"] = Text(el, "NAME"),
            ["currency"] = Text(el, "BASICCURRENCYNAME"),
            ["books_from"] = Date(el, "BOOKSFROM"),
            ["starting_from"] = Date(el, "STARTINGFROM"),
            ["ending_at"] = Date(el, "ENDINGAT"),
            ["email"] = Text(el, "EMAIL"),
            ["website"] = Text(el, "WEBSITE"),
            ["address"] = Text(el, "ADDRESS"),
            ["state"] = Text(el, "STATENAME"),
            ["pincode"] = Text(el, "PINCODE"),
            ["pan"] = Text(el, "INCOMETAXNUMBER"),
            ["gstin"] = Text(el, "GSTREGISTRATIONNUMBER"),
        }).ToList();
        log.LogInformation("Fetched {N} companies", rows.Count);
        return rows;
    }

    public async Task<List<Row>> Groups(CancellationToken ct)
    {
        var doc = await FetchCollection("Group",
            [
            "GUID","MASTERID","ALTERID","NAME","PARENT","ISREVENUE","ISDEEMEDPOSITIVE","ISSUBLEDGER"], ct);
        var rows = doc.Descendants("GROUP").Select(el => new Row
        {
            ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["group_name"] = Text(el, "NAME"),
            ["parent"] = Text(el, "PARENT"),
            ["is_revenue"] = Bool(el, "ISREVENUE"),
            ["is_deemed_positive"] = Bool(el, "ISDEEMEDPOSITIVE"),
            ["is_subledger"] = Bool(el, "ISSUBLEDGER"),
        }).Where(r => ((string)r["group_name"]!).Length > 0).ToList();
        log.LogInformation("Fetched {N} groups", rows.Count);
        return rows;
    }

    public async Task<List<Row>> Ledgers(CancellationToken ct)
    {
        var doc = await LedgerDocument(ct);
        var rows = new List<Row>();
        var bal = NewBalanceSource("ledgers");
        foreach (var el in doc.Descendants("LEDGER"))
        {
            var guid = Text(el, "GUID");
            var gstin = Text(el, "PARTYGSTIN");
            if (gstin.Length == 0) gstin = Text(el, "GSTIN");
            if (gstin.Length == 0) gstin = Text(el, "GSTREGISTRATIONNUMBER");
            rows.Add(new Row
            {
                ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["ledger_name"] = Text(el, "NAME"),
                ["parent_group"] = Text(el, "PARENT"),
                ["opening_balance"] = bal.Get(el, guid, "OPENINGBALANCE"),
                ["closing_balance"] = bal.Get(el, guid, "CLOSINGBALANCE"),
                ["balance_as_of"] = bal.AsOf,
                ["gstin"] = gstin,
                ["pan"] = Text(el, "INCOMETAXNUMBER"),
                ["state"] = Text(el, "LEDSTATENAME"),
                ["country"] = Text(el, "COUNTRYNAME"),
                ["address"] = Text(el, "ADDRESS"),
                ["pincode"] = Text(el, "PINCODE"),
                ["mobile"] = Text(el, "LEDGERMOBILE"),
                ["phone"] = Text(el, "LEDGERPHONE"),
                ["email"] = Text(el, "EMAIL"),
                ["contact_person"] = Text(el, "LEDGERCONTACT"),
                ["bank_account_number"] = Text(el, "BANKACCOUNTNUMBER"),
                ["ifsc_code"] = Text(el, "IFSCODE"),
                ["bank_name"] = Text(el, "BANKNAME"),
                ["branch_name"] = Text(el, "BRANCHNAME"),
                ["is_billwise"] = Bool(el, "ISBILLWISEON"),
                ["is_costcentre"] = Bool(el, "ISCOSTCENTRESON"),
            });
        }
        PersistBalances("ledgers", bal);
        log.LogInformation("Fetched {N} ledgers (balances: {Mode})", rows.Count,
            _fetchBalances ? "fresh from Tally" : "from last daily capture");
        return rows;
    }

    public async Task<List<Row>> VoucherTypes(CancellationToken ct)
    {
        var doc = await FetchCollection("VoucherType",
            [
            "GUID","MASTERID","ALTERID","NAME","PARENT","ADDITIONALNAME","NUMBERINGMETHOD"], ct);
        return doc.Descendants("VOUCHERTYPE").Select(el => new Row
        {
            ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["voucher_type_name"] = Text(el, "NAME"),
            ["parent"] = Text(el, "PARENT"),
            ["alias"] = Text(el, "ADDITIONALNAME"),
            ["numbering_method"] = Text(el, "NUMBERINGMETHOD"),
        }).ToList();
    }

    public async Task<List<Row>> CostCentres(CancellationToken ct)
    {
        var doc = await FetchCollection("CostCentre", [
            "GUID","MASTERID","ALTERID","NAME","PARENT","CATEGORY"], ct);
        return doc.Descendants("COSTCENTRE").Select(el => new Row
        {
            ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["cost_centre_name"] = Text(el, "NAME"),
            ["parent"] = Text(el, "PARENT"),
            ["category"] = Text(el, "CATEGORY"),
        }).ToList();
    }

    public async Task<List<Row>> CostCategories(CancellationToken ct)
    {
        var doc = await FetchCollection("CostCategory", [
            "GUID","MASTERID","ALTERID","NAME"], ct);
        return doc.Descendants("COSTCATEGORY")
            .Select(el => new Row { ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["category_name"] = Text(el, "NAME") })
            .ToList();
    }

    public async Task<List<Row>> Currencies(CancellationToken ct)
    {
        var doc = await FetchCollection("Currency",
            [
            "GUID","MASTERID","ALTERID","NAME","MAILINGNAME","EXPANDEDSYMBOL","DECIMALPLACES"], ct);
        return doc.Descendants("CURRENCY").Select(el => new Row
        {
            ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["currency_name"] = Text(el, "NAME"),
            ["symbol"] = Text(el, "EXPANDEDSYMBOL"),
            ["formal_name"] = Text(el, "MAILINGNAME"),
            ["decimal_places"] = Int(el, "DECIMALPLACES"),
        }).ToList();
    }

    public async Task<List<Row>> Units(CancellationToken ct)
    {
        var doc = await FetchCollection("Unit", [
            "GUID","MASTERID","ALTERID",
            "NAME","ORIGINALNAME","BASEUNITS","ADDITIONALUNITS","CONVERSION",
            "ISSIMPLEUNIT","DECIMALPLACES"], ct);
        return doc.Descendants("UNIT").Select(el => new Row
        {
            ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["uom_name"] = Text(el, "NAME"),
            ["original_name"] = Text(el, "ORIGINALNAME"),
            ["base_units"] = Text(el, "BASEUNITS"),
            ["additional_units"] = Text(el, "ADDITIONALUNITS"),
            ["conversion"] = Text(el, "CONVERSION"),
            ["is_simple"] = Bool(el, "ISSIMPLEUNIT"),
            ["decimal_places"] = Int(el, "DECIMALPLACES"),
        }).ToList();
    }

    public async Task<List<Row>> StockGroups(CancellationToken ct)
    {
        var doc = await FetchCollection("StockGroup", [
            "GUID","MASTERID","ALTERID","NAME","PARENT"], ct);
        return doc.Descendants("STOCKGROUP").Select(el => new Row
        {
            ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["stock_group_name"] = Text(el, "NAME"),
            ["parent"] = Text(el, "PARENT"),
        }).ToList();
    }

    public async Task<List<Row>> StockItems(CancellationToken ct)
    {
        var doc = await StockItemDocument(ct);
        var rows = new List<Row>();
        var bal = NewBalanceSource("stock_items");
        foreach (var el in doc.Descendants("STOCKITEM"))
        {
            var name = Text(el, "NAME");
            if (name.Length == 0) continue;
            var guid = Text(el, "GUID");
            rows.Add(new Row
            {
                ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["item_name"] = name,
                ["parent_group"] = Text(el, "PARENT"),
                ["category"] = Text(el, "CATEGORY"),
                ["uom"] = Text(el, "BASEUNITS"),
                ["opening_qty"] = Num(el, "OPENINGBALANCE"),
                ["opening_value"] = Num(el, "OPENINGVALUE"),
                ["opening_rate"] = Num(el, "OPENINGRATE"),
                ["closing_qty"] = bal.Get(el, guid, "CLOSINGBALANCE"),
                ["closing_value"] = bal.Get(el, guid, "CLOSINGVALUE"),
                ["closing_rate"] = bal.Get(el, guid, "CLOSINGRATE"),
                ["balance_as_of"] = bal.AsOf,
                ["gst_rate"] = Num(el, "GSTRATE"),
                ["hsn_code"] = Text(el, "HSNCODE"),
                ["description"] = Text(el, "DESCRIPTION"),
                ["alias"] = Text(el, "ADDITIONALNAME"),
            });
        }
        PersistBalances("stock_items", bal);
        log.LogInformation("Fetched {N} stock items (balances: {Mode})", rows.Count,
            _fetchBalances ? "fresh from Tally" : "from last daily capture");
        return rows;
    }

    public async Task<List<Row>> Godowns(CancellationToken ct)
    {
        var doc = await FetchCollection("Godown",
            [
            "GUID","MASTERID","ALTERID","NAME","PARENT","ADDRESS","HASNOSPACE","HASNOSTOCK"], ct);
        return doc.Descendants("GODOWN").Select(el => new Row
        {
            ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["godown_name"] = Text(el, "NAME"),
            ["parent"] = Text(el, "PARENT"),
            ["address"] = Text(el, "ADDRESS"),
            ["has_no_space"] = Bool(el, "HASNOSPACE"),
            ["has_no_stock"] = Bool(el, "HASNOSTOCK"),
        }).ToList();
    }

    /// <summary>GST rates + HSN. TallyPrime nests GSTDETAILS.LIST → STATEWISEDETAILS.LIST
    /// → RATEDETAILS.LIST; older Tally exposes flat GSTRATE/HSNCODE. Both handled.</summary>
    public async Task<List<Row>> GstRates(CancellationToken ct)
    {
        var doc = await StockItemDocument(ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants("STOCKITEM"))
        {
            var name = Text(el, "NAME");
            if (name.Length == 0) continue;

            string hsn = "", gstApplicable = "", supplyType = "", taxClass = "";
            double rate = 0;

            var gstDetail = el.Element("GSTDETAILS.LIST") ?? el.Element("GSTDETAILS");
            if (gstDetail is not null)
            {
                hsn = Text(gstDetail, "HSNCODE");
                if (hsn.Length == 0) hsn = Text(gstDetail, "HSN");
                gstApplicable = Text(gstDetail, "TAXABILITY");
                if (gstApplicable.Length == 0) gstApplicable = Text(gstDetail, "GSTAPPLICABLE");
                supplyType = Text(gstDetail, "GSTTYPEOFSUPPLY");
                taxClass = Text(gstDetail, "TAXCLASSIFICATIONNAME");
                rate = gstDetail.Descendants("RATEDETAILS.LIST")
                    .Concat(gstDetail.Descendants("RATEDETAILS"))
                    .Select(rd => Num(rd, "GSTRATE"))
                    .FirstOrDefault(v => v != 0);
            }

            if (hsn.Length == 0) hsn = Text(el, "HSNCODE");
            if (rate == 0) rate = Num(el, "GSTRATE");
            if (gstApplicable.Length == 0) gstApplicable = Text(el, "GSTAPPLICABLE");
            if (supplyType.Length == 0) supplyType = Text(el, "GSTTYPEOFSUPPLY");
            if (taxClass.Length == 0) taxClass = Text(el, "TAXCLASSIFICATIONNAME");

            rows.Add(new Row
            {
                ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["item_name"] = name,
                ["gst_rate"] = rate,
                ["hsn_code"] = hsn,
                ["gst_applicable"] = gstApplicable,
                ["supply_type"] = supplyType,
                ["tax_classification"] = taxClass,
            });
        }
        return rows;
    }

    /// <summary>Opening bill-wise balances from ledger BILLALLOCATIONS.
    ///
    /// Reads BOTH serialisation shapes: TallyPrime emits a list-valued member as
    /// &lt;BILLALLOCATIONS.LIST&gt;, but some builds/TDL emit bare
    /// &lt;BILLALLOCATIONS&gt;. Matching on local name rather than an exact
    /// XName also survives the namespace-prefix declaration
    /// <see cref="TallyXml.Sanitize"/> performs.</summary>
    public async Task<List<Row>> OpeningBills(CancellationToken ct)
    {
        var doc = await LedgerDocument(ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants("LEDGER"))
        {
            var ledger = Text(el, "NAME");
            foreach (var ba in BillAllocationElements(el))
            {
                var billRef = Text(ba, "NAME");
                if (billRef.Length == 0) continue;
                rows.Add(new Row
                {
                    ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["ledger_name"] = ledger,
                    ["bill_ref"] = billRef,
                    ["bill_date"] = Date(ba, "BILLDATE"),
                    ["bill_credit_period"] = Text(ba, "BILLCREDITPERIOD"),
                    ["bill_type"] = Text(ba, "BILLTYPE"),
                    ["is_advance"] = Bool(ba, "ISADVANCE"),
                    ["opening_amount"] = Num(ba, "OPENINGBALANCE"),
                    ["closing_amount"] = Num(ba, "CLOSINGBALANCE"),
                });
            }
        }
        log.LogInformation("Opening bills: {N} rows from {L} ledgers", rows.Count,
            doc.Descendants("LEDGER").Count());
        return rows;
    }

    /// <summary>Bill-allocation child elements of a ledger, under either
    /// serialisation (&lt;BILLALLOCATIONS.LIST&gt; or &lt;BILLALLOCATIONS&gt;).</summary>
    public static IEnumerable<XElement> BillAllocationElements(XElement ledger) =>
        ledger.Descendants().Where(e =>
            e.Name.LocalName.Equals("BILLALLOCATIONS.LIST", StringComparison.OrdinalIgnoreCase) ||
            e.Name.LocalName.Equals("BILLALLOCATIONS", StringComparison.OrdinalIgnoreCase));

    public async Task<List<Row>> StockStandardCosts(CancellationToken ct) =>
        await StandardRates("STANDARDCOSTLIST.LIST", ct);

    public async Task<List<Row>> StockStandardPrices(CancellationToken ct) =>
        await StandardRates("STANDARDPRICELIST.LIST", ct);

    private async Task<List<Row>> StandardRates(string listTag, CancellationToken ct)
    {
        var doc = await StockItemDocument(ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants("STOCKITEM"))
        {
            var item = Text(el, "NAME");
            foreach (var entry in el.Descendants(listTag))
            {
                rows.Add(new Row
                {
                    ["master_guid"] = Text(el, "GUID"),
            ["master_id"] = Int(el, "MASTERID"),
            ["alter_id"] = Int(el, "ALTERID"),
            ["item_name"] = item,
                    ["effective_date"] = Date(entry, "DATE"),
                    ["rate"] = Num(entry, "RATE"),
                });
            }
        }
        return rows;
    }

    /// <summary>Bank ledger names (parent group containing "Bank") for the
    /// bank-book fan-out — derived from the cycle's cached Ledger document,
    /// no extra Tally request.</summary>
    public async Task<HashSet<string>> BankLedgerNames(CancellationToken ct)
    {
        var doc = await LedgerDocument(ct);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in doc.Descendants("LEDGER"))
        {
            var parent = Text(el, "PARENT");
            if (parent.Contains("Bank", StringComparison.OrdinalIgnoreCase))
                set.Add(Text(el, "NAME"));
        }
        return set;
    }
}
