using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Tally;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>Phase E preflight outcome tests — fully offline: TCP layer via a
/// loopback listener, HTTP layer via a fake handler.</summary>
public class PreflightTests : IDisposable
{
    private readonly string _lockDir =
        Path.Combine(Path.GetTempPath(), "preflight-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _port;

    public PreflightTests()
    {
        Directory.CreateDirectory(_lockDir);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop(_listener);
    }

    public void Dispose()
    {
        _listener.Stop();
        try { Directory.Delete(_lockDir, true); } catch { }
    }

    private static async Task AcceptLoop(TcpListener l)
    {
        try { while (true) (await l.AcceptTcpClientAsync()).Dispose(); }
        catch { }
    }

    private sealed class FixedHandler(string xml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(xml, Encoding.UTF8, "text/xml") });
    }

    private TallyClient NewClient(string companyListXml, string configuredCompany, int? port = null)
    {
        var settings = new TallySettings
        {
            Host = "127.0.0.1",
            Port = port ?? _port,
            Company = configuredCompany,
            RequestTimeoutSeconds = 10,
            GateWaitSeconds = 5,
        };
        return new TallyClient(settings, NullLogger<TallyClient>.Instance,
            new HttpClient(new FixedHandler(companyListXml)), _lockDir)
        { DelayAsync = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
    }

    private static string Companies(params string[] names) =>
        "<ENVELOPE>" + string.Join("", names.Select(n => $"<COMPANY><NAME>{n}</NAME></COMPANY>")) + "</ENVELOPE>";

    [Fact]
    public async Task TallyNotRunning_WhenNothingListens()
    {
        // Bind then release a port so it is guaranteed unused.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var client = NewClient(Companies("X"), "Dynel", deadPort);
        var result = await client.ProbeAsync();
        Assert.False(result.Ok);
        Assert.Equal(ErrorCategory.TallyNotRunning, result.Category);
    }

    [Fact]
    public async Task CompanyNotOpen_WhenNoCompanyOpenAtAll()
    {
        using var client = NewClient(Companies(), "Dynel Electric Private Limited");
        var result = await client.ProbeAsync();
        Assert.False(result.Ok);
        Assert.Equal(ErrorCategory.TallyCompanyNotOpen, result.Category);
        Assert.DoesNotContain("<", result.Error);            // no raw XML leaked (§E9)
    }

    [Fact]
    public async Task CompanyMismatch_WhenDifferentCompanyOpen()
    {
        using var client = NewClient(Companies("Some Other Co"), "Dynel Electric Private Limited");
        var result = await client.ProbeAsync();
        Assert.False(result.Ok);
        Assert.Equal(ErrorCategory.TallyCompanyMismatch, result.Category);
        Assert.Contains("Some Other Co", result.Error);      // operator-actionable
    }

    [Fact]
    public async Task Ok_WhenConfiguredCompanyIsOpen()
    {
        using var client = NewClient(Companies("Dynel Electric Private Limited", "Other"),
            "Dynel Electric Private Limited");
        var result = await client.ProbeAsync();
        Assert.True(result.Ok);
        Assert.Contains("Dynel Electric Private Limited", result.Companies);
    }

    [Fact]
    public async Task Ok_WithAutoDiscovery_WhenNoCompanyConfigured()
    {
        using var client = NewClient(Companies("Anything"), "");
        var result = await client.ProbeAsync();
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task PreflightCancellation_PropagatesAsCancellation()
    {
        using var client = NewClient(Companies("X"), "X");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ProbeAsync(cts.Token));
    }
}
