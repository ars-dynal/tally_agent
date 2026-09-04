using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>
/// v2.4.0 — the Day Book is walked ONE DAY AT A TIME, driven by SVCURRENTDATE.
///
/// Measured against the live server on 2026-09-04: the Day Book report ignores
/// SVFROMDATE and SVTODATE entirely and reports whatever day SVCURRENTDATE
/// names. Asked for 5-Apr..7-Apr with SVCURRENTDATE=7-Apr it returned 85
/// vouchers, every one dated 7-Apr — 12.6 MB, ~148 KB per voucher.
///
/// The fake Tally here behaves the same way: it answers with the SVCURRENTDATE
/// day and ignores the range. A range-based extractor cannot pass these tests.
/// </summary>
public sealed class DailyVoucherWalkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "daily-" + Guid.NewGuid().ToString("N"));
    public DailyVoucherWalkTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Answers with vouchers dated SVCURRENTDATE, whatever FROM/TO say —
    /// which is what the real Tally does.</summary>
    private sealed class CurrentDateTally(Func<DateOnly, int> vouchersOnDay) : HttpMessageHandler
    {
        public readonly List<DateOnly> CurrentDatesAsked = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = await req.Content!.ReadAsStringAsync(ct);
            var m = Regex.Match(body, "<SVCURRENTDATE>(\\d{8})</SVCURRENTDATE>");
            var xml = new StringBuilder("<ENVELOPE>");
            if (m.Success)
            {
                var day = DateOnly.ParseExact(m.Groups[1].Value, "yyyyMMdd");
                CurrentDatesAsked.Add(day);
                for (var i = 0; i < vouchersOnDay(day); i++)
                    xml.Append($"<VOUCHER VCHTYPE=\"Sales\"><DATE>{day:yyyyMMdd}</DATE>")
                       .Append($"<VOUCHERNUMBER>V{day:yyyyMMdd}-{i}</VOUCHERNUMBER>")
                       .Append($"<GUID>g-{day:yyyyMMdd}-{i}</GUID>")
                       .Append("<ALLLEDGERENTRIES.LIST><LEDGERNAME>Cash</LEDGERNAME>")
                       .Append("<AMOUNT>-100</AMOUNT></ALLLEDGERENTRIES.LIST></VOUCHER>");
            }
            xml.Append("</ENVELOPE>");
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml") };
        }
    }

    private (VoucherExtractor Ex, CurrentDateTally Tally) Build(Func<DateOnly, int> perDay)
    {
        var handler = new CurrentDateTally(perDay);
        var client = new TallyClient(new TallySettings { Company = "Co", RequestPauseSeconds = 0 },
            NullLogger<TallyClient>.Instance, new HttpClient(handler), _dir)
        { DelayAsync = (_, _) => Task.CompletedTask };
        return (new VoucherExtractor(client, NullLogger<VoucherExtractor>.Instance), handler);
    }

    [Fact]
    public async Task AWindowIsWalkedOneDayAtATime_EveryDayAsked_NoneSkipped()
    {
        var (ex, tally) = Build(_ => 2);

        var from = new DateOnly(2026, 4, 1);
        var to = new DateOnly(2026, 4, 7);
        var result = await ex.ExtractWindow(from, to, new HashSet<string>(), CancellationToken.None);

        // One request per day, in order, none skipped.
        Assert.Equal(7, tally.CurrentDatesAsked.Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(from.AddDays).ToList(), tally.CurrentDatesAsked);
        Assert.Equal(14, result.VoucherHeaders.Count);
        Assert.Equal(0, result.OutOfWindowCount);
    }

    [Fact]
    public async Task EmptyDaysReturnEmpty_AndAreNotSkippedOrTreatedAsFailure()
    {
        // Only the 3rd of the month has anything. The walk must still ask for
        // every other day rather than stopping or jumping.
        var (ex, tally) = Build(d => d.Day == 3 ? 5 : 0);

        var result = await ex.ExtractWindow(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 5),
            new HashSet<string>(), CancellationToken.None);

        Assert.Equal(5, tally.CurrentDatesAsked.Count);
        Assert.Equal(5, result.VoucherHeaders.Count);
        Assert.Equal(0, result.OutOfWindowCount);
    }

    [Fact]
    public async Task EveryRequestCarriesSvCurrentDate_BecauseFromAndToAreIgnored()
    {
        var (ex, tally) = Build(_ => 1);
        await ex.ExtractWindow(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 6),
            new HashSet<string>(), CancellationToken.None);

        // The handler only records a day when SVCURRENTDATE is present; a
        // request relying on FROM/TO alone would record nothing and return
        // nothing, exactly as the 04-May..03-Jun window did in rc1.
        Assert.Equal(3, tally.CurrentDatesAsked.Count);
    }

    [Fact]
    public async Task AVoucherDatedOtherThanTheDayAskedFor_IsRejected()
    {
        // The guard, at its strongest: one request asks for one day, so any
        // other date in the response means the mechanism has regressed.
        var handler = new WrongDateTally();
        var client = new TallyClient(new TallySettings { Company = "Co", RequestPauseSeconds = 0 },
            NullLogger<TallyClient>.Instance, new HttpClient(handler), _dir)
        { DelayAsync = (_, _) => Task.CompletedTask };
        var ex = new VoucherExtractor(client, NullLogger<VoucherExtractor>.Instance);

        var result = await ex.ExtractWindow(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4),
            new HashSet<string>(), CancellationToken.None);

        // This is precisely the rc1 failure: asked for May, served 1-Sep.
        Assert.Equal(1, result.OutOfWindowCount);
        Assert.Empty(result.VoucherHeaders);          // nothing wrong-dated leaks through
        Assert.Equal("2026-09-01", result.ServedMinDate);
    }

    private sealed class WrongDateTally : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<ENVELOPE><VOUCHER VCHTYPE=\"Sales\"><DATE>20260901</DATE>" +
                    "<VOUCHERNUMBER>X-1</VOUCHERNUMBER><GUID>g-x</GUID></VOUCHER></ENVELOPE>",
                    Encoding.UTF8, "text/xml"),
            });
    }

    [Fact]
    public void DayBookEnvelope_CarriesSvCurrentDate_ButTrialBalanceDoesNot()
    {
        var day = new DateOnly(2026, 4, 7);
        var dayBook = TallyEnvelopes.Report("Day Book", day, day, "Co", currentDate: day);
        Assert.Contains("<SVCURRENTDATE>20260407</SVCURRENTDATE>", dayBook);

        // Period reports honour FROM/TO - probe 18 returned the 11 primary
        // groups for 1-Apr..1-Sep. Pinning SVCURRENTDATE there would collapse
        // them to a single day.
        var tb = TallyEnvelopes.Report("Trial Balance", new DateOnly(2026, 4, 1), day, "Co");
        Assert.DoesNotContain("SVCURRENTDATE", tb);
    }
}
