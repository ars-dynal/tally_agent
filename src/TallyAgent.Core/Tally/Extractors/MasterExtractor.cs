using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using static TallyAgent.Core.Tally.TallyXml;

namespace TallyAgent.Core.Tally.Extractors;

using Row = Dictionary<string, object?>;

/// <summary>Extracts the 13 master/inventory-master collections via TDL FETCH lists.
/// Field lists and fallback chains ported from the proven Python connector.</summary>
public sealed class MasterExtractor(TallyClient client, ILogger<MasterExtractor> log)
{
    private async Task<XDocument> FetchCollection(string type, string[] fields, CancellationToken ct) =>
        await client.PostAsync(TallyEnvelopes.Collection(type, fields, client.Company), ct);

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
        var doc = await FetchCollection("Ledger", [
            "GUID","MASTERID","ALTERID",
            "NAME","PARENT","OPENINGBALANCE","CLOSINGBALANCE","PARTYGSTIN","GSTIN",
            "GSTREGISTRATIONNUMBER","INCOMETAXNUMBER","LEDSTATENAME","COUNTRYNAME","ADDRESS",
            "PINCODE","LEDGERMOBILE","LEDGERPHONE","EMAIL","LEDGERCONTACT","BANKACCOUNTNUMBER",
            "IFSCODE","BANKNAME","BRANCHNAME","ISBILLWISEON","ISCOSTCENTRESON"], ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants("LEDGER"))
        {
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
                ["opening_balance"] = Num(el, "OPENINGBALANCE"),
                ["closing_balance"] = Num(el, "CLOSINGBALANCE"),
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
        log.LogInformation("Fetched {N} ledgers", rows.Count);
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
        var doc = await FetchCollection("StockItem", [
            "GUID","MASTERID","ALTERID",
            "NAME","PARENT","CATEGORY","BASEUNITS","OPENINGBALANCE","OPENINGVALUE",
            "OPENINGRATE","CLOSINGBALANCE","CLOSINGVALUE","CLOSINGRATE","GSTRATE",
            "HSNCODE","DESCRIPTION","ADDITIONALNAME"], ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants("STOCKITEM"))
        {
            var name = Text(el, "NAME");
            if (name.Length == 0) continue;
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
                ["closing_qty"] = Num(el, "CLOSINGBALANCE"),
                ["closing_value"] = Num(el, "CLOSINGVALUE"),
                ["closing_rate"] = Num(el, "CLOSINGRATE"),
                ["gst_rate"] = Num(el, "GSTRATE"),
                ["hsn_code"] = Text(el, "HSNCODE"),
                ["description"] = Text(el, "DESCRIPTION"),
                ["alias"] = Text(el, "ADDITIONALNAME"),
            });
        }
        log.LogInformation("Fetched {N} stock items", rows.Count);
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
        var doc = await FetchCollection("StockItem", [
            "GUID","MASTERID","ALTERID",
            "NAME","GSTRATE","HSNCODE","GSTAPPLICABLE","GSTTYPEOFSUPPLY",
            "TAXCLASSIFICATIONNAME","GSTDETAILS.LIST"], ct);
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

    /// <summary>Opening bill-wise balances from ledger BILLALLOCATIONS.</summary>
    public async Task<List<Row>> OpeningBills(CancellationToken ct)
    {
        var doc = await FetchCollection("Ledger",
            [
            "GUID","MASTERID","ALTERID","NAME","BILLALLOCATIONS.LIST"], ct);
        var rows = new List<Row>();
        foreach (var el in doc.Descendants("LEDGER"))
        {
            var ledger = Text(el, "NAME");
            foreach (var ba in el.Descendants("BILLALLOCATIONS.LIST"))
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
                    ["bill_type"] = Text(ba, "BILLTYPE"),
                    ["opening_amount"] = Num(ba, "OPENINGBALANCE"),
                    ["closing_amount"] = Num(ba, "CLOSINGBALANCE"),
                });
            }
        }
        return rows;
    }

    public async Task<List<Row>> StockStandardCosts(CancellationToken ct) =>
        await StandardRates("STANDARDCOSTLIST.LIST", ct);

    public async Task<List<Row>> StockStandardPrices(CancellationToken ct) =>
        await StandardRates("STANDARDPRICELIST.LIST", ct);

    private async Task<List<Row>> StandardRates(string listTag, CancellationToken ct)
    {
        var doc = await FetchCollection("StockItem", [
            "GUID","MASTERID","ALTERID","NAME", listTag], ct);
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
}
