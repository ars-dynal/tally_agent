using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Tally;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>
/// v2.4.0 — the report envelope, and why voucher scoping broke.
///
/// Measured against the live Tally server on 2026-09-04, same company, same
/// dates, seconds apart:
///
///   TALLYREQUEST=Export Data + EXPORTDATA/REQUESTDESC/REPORTNAME   REFUSED
///   TALLYREQUEST=Export + TYPE=Data + ID=&lt;report&gt;                  ACCEPTED
///
///   Voucher collection, 1 day, no filter          4,355 vouchers  5,640,137 ch
///   Voucher collection, 1 day, ##SVFromDate       4,355 vouchers  BYTE-IDENTICAL
///   Voucher collection, 1 day, literal date           4 vouchers      6,874 ch
///   Day Book report, 1 day                        full detail       424,860 ch
///   Trial Balance report                          DSPACCNAME x11      3,055 ch
///
/// Day Book shows 3 vouchers on that day. These tests pin the two conclusions
/// so neither can be undone by accident.
/// </summary>
public class ReportEnvelopeTests
{
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 1);

    [Fact]
    public void Report_UsesTheShapeTallyAccepts_NotTheOneItRefuses()
    {
        var xml = TallyEnvelopes.Report("Day Book", From, To, "Dynalektric Equipment Private Limited");

        // Accepted shape.
        Assert.Contains("<TALLYREQUEST>Export</TALLYREQUEST>", xml);
        Assert.Contains("<TYPE>Data</TYPE>", xml);
        Assert.Contains("<ID>Day Book</ID>", xml);
        Assert.Contains("<BODY><DESC><STATICVARIABLES>", xml);

        // The shape Tally refuses outright. Every report request this agent made
        // from its first commit until v2.4.0 used it, which is why trial_balance
        // had never once come from the report route.
        Assert.DoesNotContain("Export Data", xml);
        Assert.DoesNotContain("EXPORTDATA", xml);
        Assert.DoesNotContain("REQUESTDESC", xml);
        Assert.DoesNotContain("REPORTNAME", xml);
    }

    [Fact]
    public void Report_StillCarriesTheDateWindowAndCompany()
    {
        var xml = TallyEnvelopes.Report("Trial Balance", new DateOnly(2026, 4, 1), To, "Acme Ltd");
        Assert.Contains("<SVFROMDATE>20260401</SVFROMDATE>", xml);
        Assert.Contains("<SVTODATE>20260901</SVTODATE>", xml);
        Assert.Contains("<SVCURRENTCOMPANY>Acme Ltd</SVCURRENTCOMPANY>", xml);
    }

    [Fact]
    public void BillsReport_IsJustAReport_NoSeparateShapeToGetWrong()
    {
        Assert.Equal(
            TallyEnvelopes.Report("Bills Payable", From, To, "Acme Ltd"),
            TallyEnvelopes.BillsReport("Bills Payable", From, To, "Acme Ltd"));
    }

    // ── counting collection: literal dates, never ##SVFromDate ───────────

    [Fact]
    public void CountingCollection_UsesLiteralDates_BecauseTheVariablesDoNotResolve()
    {
        var xml = TallyEnvelopes.VoucherDatesForCounting(
            new DateOnly(2019, 4, 1), new DateOnly(2020, 3, 31), "Acme Ltd");

        // A filter referencing ##SVFromDate is INERT here - the same collection
        // with and without it came back byte-identical, serving 4,355 vouchers
        // for a one-day window. Literal dates returned 4.
        Assert.DoesNotContain("##SVFromDate", xml);
        Assert.DoesNotContain("##SVToDate", xml);
        Assert.Contains("$$Date:\"1-Apr-2019\"", xml);
        Assert.Contains("$$Date:\"31-Mar-2020\"", xml);
        Assert.Contains("<TYPE>Voucher</TYPE>", xml);
    }

    [Fact]
    public void TallyDate_IsTheFormTdlFormulasAccept()
    {
        Assert.Equal("1-Sep-2026", TallyEnvelopes.TallyDate(new DateOnly(2026, 9, 1)));
        Assert.Equal("31-Mar-2020", TallyEnvelopes.TallyDate(new DateOnly(2020, 3, 31)));
    }

    // ── the refusal that started it, still detected ──────────────────────

    [Fact]
    public void TheRefusalTheOldShapeProduced_IsStillRecognised()
    {
        // Verbatim from probes 12 and 13.
        var doc = XDocument.Parse("<RESPONSE>Unknown Request, cannot be processed</RESPONSE>");
        Assert.Equal("Unknown Request, cannot be processed", TallyXml.FindRequestError(doc));
    }

    // ── window sizing, from measured bytes rather than habit ─────────────

    [Fact]
    public void DefaultChunk_KeepsAWindowWellUnderTheResponseCap()
    {
        var s = new TallySettings();

        // Measured: one day of Day Book = 424,860 bytes for 3 vouchers, so
        // ~141,620 bytes per voucher; ~28 vouchers/day observed. A 7-day window
        // is therefore ~28 MB against a 256 MB cap - roughly 9x headroom, which
        // is what absorbs a busy month-end.
        const double bytesPerVoucher = 424_860d / 3;
        const double vouchersPerDay = 28d;
        var windowBytes = s.FullSyncChunkDays * vouchersPerDay * bytesPerVoucher;
        var capBytes = s.MaxResponseMb * 1024L * 1024L;

        Assert.True(windowBytes < capBytes / 4,
            $"A default {s.FullSyncChunkDays}-day window is ~{windowBytes / 1024 / 1024:F0} MB " +
            $"against a {s.MaxResponseMb} MB cap; that leaves too little headroom for a busy month.");
    }
}
