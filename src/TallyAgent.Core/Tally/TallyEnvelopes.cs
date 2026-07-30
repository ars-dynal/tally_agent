using System.Text;

namespace TallyAgent.Core.Tally;

/// <summary>Builders for the two Tally request families: TDL collections
/// (masters) and report exports (Day Book, Trial Balance, ...).</summary>
public static class TallyEnvelopes
{
    /// <summary>Collection request: &lt;TYPE&gt;Ledger&lt;/TYPE&gt; + FETCH list.</summary>
    public static string Collection(string collectionType, IEnumerable<string> fetchFields, string? company)
    {
        var sb = new StringBuilder(1024);
        sb.Append("<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST>")
          .Append("<TYPE>Collection</TYPE><ID>AgentCollection</ID></HEADER>")
          .Append("<BODY><DESC><STATICVARIABLES>")
          .Append("<SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>");
        if (!string.IsNullOrEmpty(company))
            sb.Append("<SVCURRENTCOMPANY>").Append(TallyXml.XmlEscape(company)).Append("</SVCURRENTCOMPANY>");
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
        sb.Append("<ENVELOPE><HEADER><TALLYREQUEST>Export Data</TALLYREQUEST></HEADER>")
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

    /// <summary>Lightweight company-list probe (also the connection test).</summary>
    public static string CompanyList() =>
        "<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST>" +
        "<TYPE>Collection</TYPE><ID>CompanyList</ID></HEADER><BODY><DESC>" +
        "<STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES>" +
        "<TDL><TDLMESSAGE><COLLECTION NAME=\"CompanyList\"><TYPE>Company</TYPE>" +
        "<FETCH>NAME</FETCH></COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>";
}
