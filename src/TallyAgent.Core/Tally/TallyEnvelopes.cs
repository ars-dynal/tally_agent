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

    /// <summary>
    /// Report export: "Day Book", "Trial Balance", "Balance Sheet",
    /// "Profit and Loss A/c", "Stock Summary", ...
    ///
    /// THE SHAPE MATTERS, AND IT WAS WRONG UNTIL v2.4.0.
    ///
    /// Every report request this agent made from its first commit until v2.3.0
    /// used TALLYREQUEST=Export Data with EXPORTDATA/REQUESTDESC/REPORTNAME,
    /// and Tally refused ALL of them with
    /// "&lt;RESPONSE&gt;Unknown Request, cannot be processed&lt;/RESPONSE&gt;".
    /// Measured 2026-09-04: that shape refused, this one accepted, same server,
    /// same company, same dates, seconds apart.
    ///
    /// The refusal is why trial_balance had never once come from the report
    /// route, why the bills envelope hunt never converged, and why the Day Book
    /// voucher path was abandoned for a Voucher collection in aeb6dca. One
    /// wrong element name, three separate investigations.
    ///
    /// The accepted shape is the SAME envelope Collection requests use — the
    /// report name goes in ID, and the static variables live under BODY/DESC:
    ///
    ///     &lt;HEADER&gt;&lt;TALLYREQUEST&gt;Export&lt;/TALLYREQUEST&gt;
    ///             &lt;TYPE&gt;Data&lt;/TYPE&gt;&lt;ID&gt;Day Book&lt;/ID&gt;&lt;/HEADER&gt;
    ///     &lt;BODY&gt;&lt;DESC&gt;&lt;STATICVARIABLES&gt;...&lt;/STATICVARIABLES&gt;&lt;/DESC&gt;&lt;/BODY&gt;
    ///
    /// Reports honour SVFROMDATE/SVTODATE. Voucher COLLECTIONS do not (they
    /// ignore SVFROMDATE and serve from the financial-year start), which is the
    /// whole reason windowing stopped working.
    /// </summary>
    public static string Report(string reportName, DateOnly? from = null, DateOnly? to = null, string? company = null)
    {
        var sb = new StringBuilder(512);
        sb.Append("<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST>")
          .Append("<TYPE>Data</TYPE><ID>").Append(TallyXml.XmlEscape(reportName)).Append("</ID></HEADER>")
          .Append("<BODY><DESC><STATICVARIABLES>")
          .Append("<SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>");
        if (!string.IsNullOrEmpty(company))
            sb.Append("<SVCURRENTCOMPANY>").Append(TallyXml.XmlEscape(company)).Append("</SVCURRENTCOMPANY>");
        if (from is { } f)
            sb.Append("<SVFROMDATE>").Append(f.ToString("yyyyMMdd")).Append("</SVFROMDATE>");
        if (to is { } t)
            sb.Append("<SVTODATE>").Append(t.ToString("yyyyMMdd")).Append("</SVTODATE>");
        sb.Append("</STATICVARIABLES></DESC></BODY></ENVELOPE>");
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
    public static string VoucherCollection(DateOnly from, DateOnly to, string? company,
        bool includeLegacyLists = false)
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
          .Append("<FETCH>DATE,VOUCHERTYPENAME,VOUCHERNUMBER,REFERENCE,NARRATION,PARTYLEDGERNAME,GUID,MASTERID,ALTERID,ISCANCELLED,ISOPTIONAL,AMOUNT</FETCH>");

        // Explicit dotted sub-object fields ONLY (the exact set the extractor
        // reads). Wildcard fetches (ALLLEDGERENTRIES.* etc.) made Tally
        // serialize EVERY field of EVERY nested object — including deep GST/
        // tax structures nobody consumed — which is what froze the Tally UI
        // during extraction. Same technique as tally-database-loader.
        //
        // v2.0.5: ONLY the ALL*ENTRIES shape is requested by default. v2.0.3/4
        // also fetched the legacy LEDGERENTRIES/INVENTORYENTRIES lists, which
        // made Tally serialize every line TWICE (the extractor then threw one
        // copy away). Old builds that lack ALL*ENTRIES can opt in via
        // tally.voucherFetchLegacyLists.
        var prefixes = includeLegacyLists
            ? new[] { ("ALLLEDGERENTRIES", "ALLINVENTORYENTRIES"), ("LEDGERENTRIES", "INVENTORYENTRIES") }
            : new[] { ("ALLLEDGERENTRIES", "ALLINVENTORYENTRIES") };

        foreach (var (led, inv) in prefixes)
        {
            sb.Append("<FETCH>")
              .Append(led).Append(".LEDGERNAME,").Append(led).Append(".AMOUNT,").Append(led).Append(".ISDEEMEDPOSITIVE")
              .Append("</FETCH>");
            sb.Append("<FETCH>")
              .Append(led).Append(".BILLALLOCATIONS.NAME,").Append(led).Append(".BILLALLOCATIONS.AMOUNT,")
              .Append(led).Append(".BILLALLOCATIONS.BILLTYPE")
              .Append("</FETCH>");
            sb.Append("<FETCH>")
              .Append(led).Append(".BANKALLOCATIONS.TRANSACTIONTYPE,").Append(led).Append(".BANKALLOCATIONS.INSTRUMENTDATE,")
              .Append(led).Append(".BANKALLOCATIONS.INSTRUMENTNUMBER,").Append(led).Append(".BANKALLOCATIONS.BANKNAME,")
              .Append(led).Append(".BANKALLOCATIONS.AMOUNT,").Append(led).Append(".BANKALLOCATIONS.BANKERSDATE")
              .Append("</FETCH>");
            sb.Append("<FETCH>")
              .Append(led).Append(".COSTCENTREALLOCATIONS.NAME,").Append(led).Append(".COSTCENTREALLOCATIONS.AMOUNT,")
              .Append(led).Append(".CATEGORYALLOCATIONS.CATEGORY,")
              .Append(led).Append(".CATEGORYALLOCATIONS.COSTCENTREALLOCATIONS.NAME,")
              .Append(led).Append(".CATEGORYALLOCATIONS.COSTCENTREALLOCATIONS.AMOUNT")
              .Append("</FETCH>");
            sb.Append("<FETCH>")
              .Append(inv).Append(".STOCKITEMNAME,").Append(inv).Append(".ACTUALQTY,").Append(inv).Append(".RATE,")
              .Append(inv).Append(".AMOUNT,").Append(inv).Append(".GODOWNNAME,").Append(inv).Append(".BATCHALLOCATIONS.GODOWNNAME")
              .Append("</FETCH>");
        }

        sb.Append("</COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>");
        return sb.ToString();
    }

    /// <summary>
    /// Bills Outstanding report export ("Bills Payable" / "Bills Receivable").
    ///
    /// This is the ONLY place Tally hands over per-bill detail — bill date,
    /// reference number, party, pending amount, due date and Tally's OWN
    /// overdue-day count. The outstanding_payables / outstanding_receivables
    /// datasets are built from ledger CLOSINGBALANCE instead
    /// (<see cref="Collection"/> of Ledger), which is why they reconcile exactly
    /// to the trial balance and carry no bill detail at all. The two are
    /// complementary, not alternatives: nothing here replaces them.
    ///
    /// SVFROMDATE/SVTODATE bound the ageing computation, so the report is
    /// "as of" SVTODATE. Remember the active period still governs
    /// (see CLAUDE.md) — a range outside it returns a valid, EMPTY response.
    /// </summary>
    public static string BillsReport(string reportName, DateOnly from, DateOnly to, string? company) =>
        Report(reportName, from, to, company);

    /// <summary>
    /// Fallback for an empty Bills Outstanding report export: the Bills
    /// collection, which is period-bound and carries the same per-bill fields
    /// EXCEPT Tally's computed overdue-day count (a report-only column). The
    /// extractor leaves overdue_days null rather than computing its own, and
    /// stamps the row source so the warehouse can tell the two paths apart.
    /// </summary>
    public static string BillsCollection(string? company, DateOnly from, DateOnly to) =>
        Collection("Bills",
            ["NAME", "PARENT", "BILLDATE", "BILLCREDITPERIOD", "CLOSINGBALANCE",
             "OPENINGBALANCE", "BILLTYPE", "ISADVANCE"],
            company, from, to);

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

    /// <summary>
    /// Voucher DATEs for a range, for counting only.
    ///
    /// Uses a LITERAL date filter, not ##SVFromDate. Measured 2026-09-04: the
    /// same collection with ##SVFromDate returned 4,355 vouchers for a one-day
    /// window (byte-identical to sending no filter at all), while a literal
    /// date returned 4. The filter mechanism was never broken — the variables
    /// simply do not resolve inside a TDL formula here, so the filter was inert
    /// and Tally served the financial year instead.
    ///
    /// &lt;FILTER&gt; and &lt;FILTERS&gt; behave identically, so the element name
    /// was not the problem either.
    ///
    /// This is the agent's INDEPENDENT count of what Tally holds, used to prove
    /// a walk was complete rather than assuming it. Roughly 1.3 KB per voucher
    /// (Tally serialises a fixed field set whatever FETCH asks for), so a
    /// financial year costs a few MB — cheap enough to run once per full sync,
    /// far too expensive to run every cycle.
    /// </summary>
    public static string VoucherDatesForCounting(DateOnly from, DateOnly to, string? company)
    {
        var sb = new StringBuilder(1024);
        sb.Append("<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST>")
          .Append("<TYPE>Collection</TYPE><ID>AgentVoucherDates</ID></HEADER>")
          .Append("<BODY><DESC><STATICVARIABLES>")
          .Append("<SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>");
        if (!string.IsNullOrEmpty(company))
            sb.Append("<SVCURRENTCOMPANY>").Append(TallyXml.XmlEscape(company)).Append("</SVCURRENTCOMPANY>");
        sb.Append("<SVFROMDATE>").Append(from.ToString("yyyyMMdd")).Append("</SVFROMDATE>")
          .Append("<SVTODATE>").Append(to.ToString("yyyyMMdd")).Append("</SVTODATE>")
          .Append("</STATICVARIABLES><TDL><TDLMESSAGE>")
          .Append("<COLLECTION NAME=\"AgentVoucherDates\"><TYPE>Voucher</TYPE>")
          .Append("<FETCH>DATE</FETCH><FILTER>AgentVoucherDateRange</FILTER></COLLECTION>")
          .Append("<SYSTEM TYPE=\"Formulae\" NAME=\"AgentVoucherDateRange\">")
          .Append("$Date &gt;= $$Date:\"").Append(TallyDate(from)).Append("\"")
          .Append(" AND $Date &lt;= $$Date:\"").Append(TallyDate(to)).Append("\"")
          .Append("</SYSTEM></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>");
        return sb.ToString();
    }

    /// <summary>The date form Tally's TDL formulas accept: 1-Sep-2026.</summary>
    internal static string TallyDate(DateOnly d) =>
        d.ToString("d-MMM-yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The open company's period. Tally bounds EVERY export by the active
    /// period (Alt+F2) regardless of SVFROMDATE/SVTODATE: a request outside it
    /// returns a valid, EMPTY response with no error — which is exactly how six
    /// years once looked empty for three weeks (see CLAUDE.md). Anyone with the
    /// Tally UI open can change it at any moment, so it is read at the start of
    /// every run rather than assumed.
    /// </summary>
    public static string CompanyPeriod(string? company) =>
        Collection("Company",
            ["NAME", "STARTINGFROM", "ENDINGAT", "BOOKSFROM"], company);

    /// <summary>Lightweight company-list probe (also the connection test).</summary>
    public static string CompanyList() =>
        "<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST>" +
        "<TYPE>Collection</TYPE><ID>CompanyList</ID></HEADER><BODY><DESC>" +
        "<STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES>" +
        "<TDL><TDLMESSAGE><COLLECTION NAME=\"CompanyList\"><TYPE>Company</TYPE>" +
        "<FETCH>NAME</FETCH></COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>";
}