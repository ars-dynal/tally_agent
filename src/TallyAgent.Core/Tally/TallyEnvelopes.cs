using System.Text;

namespace TallyAgent.Core.Tally;

/// <summary>Builders for Tally XML requests.</summary>
public static class TallyEnvelopes
{
    /// <summary>Collection request: &lt;TYPE&gt;Ledger&lt;/TYPE&gt; + FETCH list.</summary>
    public static string Collection(string collectionType, IEnumerable<string> fetchFields, string? company,
        DateOnly? from = null, DateOnly? to = null)
    {
        var sb = new StringBuilder(1024);
        sb.Append("<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST>")
          .Append("<TYPE>Collection</TYPE><ID>AgentCollection</ID></HEADER>")
          .Append("<BODY><DESC><STATICVARIABLES>")
          .Append("<SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>");
        if (!string.IsNullOrEmpty(company))
            sb.Append("<SVCURRENTCOMPANY>").Append(TallyXml.XmlEscape(company)).Append("</SVCURRENTCOMPANY>");
        if (from is { } fromDate)
            sb.Append("<SVFROMDATE>").Append(fromDate.ToString("yyyyMMdd")).Append("</SVFROMDATE>");
        if (to is { } toDate)
            sb.Append("<SVTODATE>").Append(toDate.ToString("yyyyMMdd")).Append("</SVTODATE>");
        sb.Append("</STATICVARIABLES><TDL><TDLMESSAGE>")
          .Append("<COLLECTION NAME=\"AgentCollection\"><TYPE>")
          .Append(collectionType).Append("</TYPE>");
        foreach (var f in fetchFields)
            sb.Append("<FETCH>").Append(f).Append("</FETCH>");
        sb.Append("</COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>");
        return sb.ToString();
    }

    /// <summary>Report export: "Day Book", "Trial Balance", "Balance Sheet",
    /// "Profit and Loss A/c", "Stock Summary", ...</summary>
    public static string Report(string reportName, DateOnly? from = null, DateOnly? to = null, string? company = null)
    {
        var sb = new StringBuilder(512);
        sb.Append("<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export Data</TALLYREQUEST></HEADER>")
          .Append("<BODY><EXPORTDATA><REQUESTDESC><REPORTNAME>")
          .Append(reportName)
          .Append("</REPORTNAME><STATICVARIABLES>");
        if (!string.IsNullOrEmpty(company))
            sb.Append("<SVCURRENTCOMPANY>").Append(TallyXml.XmlEscape(company)).Append("</SVCURRENTCOMPANY>");
        if (from is { } f)
            sb.Append("<SVFROMDATE>").Append(f.ToString("yyyyMMdd")).Append("</SVFROMDATE>");
        if (to is { } t)
            sb.Append("<SVTODATE>").Append(t.ToString("yyyyMMdd")).Append("</SVTODATE>");
        sb.Append("<SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>")
          .Append("</STATICVARIABLES></REQUESTDESC></EXPORTDATA></BODY></ENVELOPE>");
        return sb.ToString();
    }

    /// <summary>
    /// Export vouchers through an explicit TDL collection instead of relying on
    /// the currently selected Day Book period in the interactive Tally session.
    /// The window is applied ONLY through SVFROMDATE/SVTODATE: a Voucher-type
    /// collection is period-bound, so Tally serves it from the voucher date
    /// index. The previous explicit &lt;FILTER&gt; formula ($Date &gt;= ... AND
    /// $Date &lt;= ...) forced Tally to materialize and scan the ENTIRE voucher
    /// file (all years) on every window, which is why even 4-day windows timed
    /// out identically — the cost was company-wide, not window-bound. The
    /// extractor still validates each voucher's DATE client-side, so any
    /// out-of-window voucher a Tally build might leak is skipped and logged.
    /// </summary>
    public static string VoucherCollection(DateOnly from, DateOnly to, string? company)
    {
        var sb = new StringBuilder(4096);
        sb.Append("<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST>")
          .Append("<TYPE>Collection</TYPE><ID>AgentVoucherCollection</ID></HEADER>")
          .Append("<BODY><DESC><STATICVARIABLES>")
          .Append("<SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>")
          .Append("<SVFROMDATE>").Append(from.ToString("yyyyMMdd")).Append("</SVFROMDATE>")
          .Append("<SVTODATE>").Append(to.ToString("yyyyMMdd")).Append("</SVTODATE>");

        if (!string.IsNullOrEmpty(company))
            sb.Append("<SVCURRENTCOMPANY>").Append(TallyXml.XmlEscape(company)).Append("</SVCURRENTCOMPANY>");

        sb.Append("</STATICVARIABLES><TDL><TDLMESSAGE>")
          .Append("<COLLECTION NAME=\"AgentVoucherCollection\"><TYPE>Voucher</TYPE>")
          .Append("<FETCH>DATE,VOUCHERTYPENAME,VOUCHERNUMBER,REFERENCE,NARRATION,PARTYLEDGERNAME,GUID,MASTERID,ALTERID,ISCANCELLED,ISOPTIONAL,AMOUNT</FETCH>")
          .Append("<FETCH>ALLLEDGERENTRIES.*,LEDGERENTRIES.*,ALLINVENTORYENTRIES.*,INVENTORYENTRIES.*</FETCH>")
          .Append("<FETCH>BILLALLOCATIONS.*,BANKALLOCATIONS.*,CATEGORYALLOCATIONS.*,COSTCENTREALLOCATIONS.*,BATCHALLOCATIONS.*</FETCH>")
          .Append("</COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>");
        return sb.ToString();
    }

    /// <summary>Company-wide AlterID watermarks: ALTMSTID (masters) and ALTVCHID
    /// (vouchers) change whenever ANY master/voucher is created, edited or
    /// deleted. One tiny request lets the agent skip whole extraction phases
    /// when nothing changed (reference-proven change gate).</summary>
    public static string CompanyAlterIds(string? company)
    {
        var sb = new StringBuilder(512);
        sb.Append("<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST>")
          .Append("<TYPE>Collection</TYPE><ID>AgentCompanyAlterIds</ID></HEADER>")
          .Append("<BODY><DESC><STATICVARIABLES>")
          .Append("<SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>");
        if (!string.IsNullOrEmpty(company))
            sb.Append("<SVCURRENTCOMPANY>").Append(TallyXml.XmlEscape(company)).Append("</SVCURRENTCOMPANY>");
        sb.Append("</STATICVARIABLES><TDL><TDLMESSAGE>")
          .Append("<COLLECTION NAME=\"AgentCompanyAlterIds\"><TYPE>Company</TYPE>")
          .Append("<FETCH>NAME,ALTMSTID,ALTVCHID</FETCH>")
          .Append("</COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>");
        return sb.ToString();
    }

    /// <summary>Lightweight company-list probe (also the connection test).</summary>
    public static string CompanyList() =>
        "<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST>" +
        "<TYPE>Collection</TYPE><ID>CompanyList</ID></HEADER><BODY><DESC>" +
        "<STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES>" +
        "<TDL><TDLMESSAGE><COLLECTION NAME=\"CompanyList\"><TYPE>Company</TYPE>" +
        "<FETCH>NAME</FETCH></COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>";
}