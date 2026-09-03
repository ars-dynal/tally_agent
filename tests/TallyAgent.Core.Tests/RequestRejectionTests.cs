using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Tally;
using TallyAgent.Core.Tally.Extractors;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>
/// v2.2.0 — a REFUSAL is not an empty result.
///
/// `&lt;RESPONSE&gt;Unknown Request, cannot be processed&lt;/RESPONSE&gt;` comes
/// back as HTTP 200, is well-formed XML, parses cleanly and contains zero rows.
/// Every extractor that says `if (rows.Count == 0) → fall back` therefore treated
/// a rejected request as an empty report: it silently ran a different code path
/// and returned a plausible number, and the zero-row guard never fired because
/// the fallback produced rows.
///
/// Same disease as the silently-ignored FETCH entry in CLAUDE.md — a valid
/// response that means "no" and reads as "nothing". Detected centrally in
/// TallyClient, never per dataset.
/// </summary>
public sealed class RequestRejectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rej-tests-" + Guid.NewGuid().ToString("N"));
    public RequestRejectionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Handler(string reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(reply, Encoding.UTF8, "text/xml") });
        }
    }

    private TallyClient Client(string reply, out Handler handler)
    {
        handler = new Handler(reply);
        return new TallyClient(new TallySettings { Company = "Co", RequestPauseSeconds = 0 },
            NullLogger<TallyClient>.Instance, new HttpClient(handler), _dir)
        { DelayAsync = (_, _) => Task.CompletedTask };
    }

    // ── detection ────────────────────────────────────────────────────────

    [Fact]
    public void UnknownRequest_IsDetectedAsARefusal()
    {
        var doc = XDocument.Parse("<RESPONSE>Unknown Request, cannot be processed</RESPONSE>");
        Assert.Equal("Unknown Request, cannot be processed", TallyXml.FindRequestError(doc));
    }

    [Fact]
    public void LineError_IsDetectedAnywhereInTheDocument()
    {
        var doc = XDocument.Parse(
            "<ENVELOPE><BODY><LINEERROR>Could not find Report 'Bills Payable'!</LINEERROR></BODY></ENVELOPE>");
        Assert.Equal("Could not find Report 'Bills Payable'!", TallyXml.FindRequestError(doc));
    }

    [Fact]
    public void RealDataResponses_AreNeverMistakenForRefusals()
    {
        Assert.Null(TallyXml.FindRequestError(XDocument.Parse(
            "<ENVELOPE><LEDGER><NAME>Cash</NAME></LEDGER></ENVELOPE>")));
        // An empty-but-valid export is NOT a refusal — the fallback must still
        // be allowed to run for a genuinely empty report.
        Assert.Null(TallyXml.FindRequestError(XDocument.Parse("<ENVELOPE/>")));
        // A RESPONSE with children is an import acknowledgement, not an error.
        Assert.Null(TallyXml.FindRequestError(XDocument.Parse(
            "<RESPONSE><CREATED>3</CREATED><ALTERED>0</ALTERED></RESPONSE>")));
    }

    // ── the client raises, so no extractor can fall back on a refusal ────

    [Fact]
    public async Task RefusedRequest_ThrowsInsteadOfReturningZeroRows()
    {
        using var client = Client("<RESPONSE>Unknown Request, cannot be processed</RESPONSE>", out _);

        var ex = await Assert.ThrowsAsync<TallyException>(() => client.PostAsync("<ENVELOPE/>"));

        Assert.Equal(ErrorCategory.TallyRequestRejected, ex.Category);
        Assert.Contains("Unknown Request", ex.Message);
        // One dataset fails; the cycle carries on.
        Assert.False(ex.IsRunEnding);
        // The body travels with the exception so a diagnostic can show the
        // refusal it tripped on without asking Tally a second time.
        Assert.Contains("Unknown Request", ex.ResponseText);
    }

    [Fact]
    public async Task RefusedRequest_IsNotRetried()
    {
        using var client = Client("<RESPONSE>Unknown Request, cannot be processed</RESPONSE>", out var handler);
        await Assert.ThrowsAsync<TallyException>(() => client.PostAsync("<ENVELOPE/>"));
        Assert.Equal(1, handler.Calls);   // deterministic refusal — retrying is waste
    }

    /// <summary>The regression this whole change exists to prevent: a refused
    /// report must NOT silently become a fallback result.</summary>
    [Fact]
    public async Task RefusedReport_DoesNotSilentlyRouteToTheFallback()
    {
        using var client = Client("<RESPONSE>Unknown Request, cannot be processed</RESPONSE>", out var handler);
        var reports = new ReportExtractor(client, NullLogger<ReportExtractor>.Instance);

        var ex = await Assert.ThrowsAsync<TallyException>(() =>
            reports.TrialBalance(new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 3), CancellationToken.None));

        Assert.Equal(ErrorCategory.TallyRequestRejected, ex.Category);
        // Exactly one request: it never reached TrialBalanceFromLedgers.
        Assert.Equal(1, handler.Calls);
    }

    // ── source column ────────────────────────────────────────────────────

    [Fact]
    public async Task TrialBalance_RecordsWhichRouteProducedTheRows()
    {
        // A genuinely empty report (valid, no refusal) still falls back — and
        // now says so. Both routes derive from the same ledger balances, so
        // without this column a fallback result is indistinguishable from the
        // report and reconciles just as well.
        using var client = Client(
            "<ENVELOPE><LEDGER><NAME>Cash</NAME><PARENT>Cash-in-Hand</PARENT>" +
            "<CLOSINGBALANCE>-250.50</CLOSINGBALANCE></LEDGER></ENVELOPE>", out var handler);
        var reports = new ReportExtractor(client, NullLogger<ReportExtractor>.Instance);

        var rows = await reports.TrialBalance(
            new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 3), CancellationToken.None);

        Assert.Equal("ledger_collection", Assert.Single(rows)["source"]);
        Assert.Equal(2, handler.Calls);   // report attempt, then the fallback
    }

    [Fact]
    public void EveryFallbackBearingReport_TagsItsRowsWithASource()
    {
        // Bills rows from the report route.
        var billRows = ReportExtractor.ParseBillsReport(XDocument.Parse(
            "<ENVELOPE><BILLFIXED><BILLREF>X</BILLREF><BILLAMT>10</BILLAMT></BILLFIXED></ENVELOPE>"),
            new DateOnly(2026, 9, 3));
        Assert.Equal("report", Assert.Single(billRows)["source"]);
    }
}
