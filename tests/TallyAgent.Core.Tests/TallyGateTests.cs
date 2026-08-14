using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Tally;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>Phase D/F tests: single-flight Tally request gate, gate release on
/// timeout/exception/cancellation, retry budgets, response size cap — all
/// offline via a fake HttpMessageHandler and injected zero delays.</summary>
public class TallyGateTests : IDisposable
{
    private readonly string _lockDir =
        Path.Combine(Path.GetTempPath(), "gate-tests-" + Guid.NewGuid().ToString("N"));

    public TallyGateTests() => Directory.CreateDirectory(_lockDir);
    public void Dispose() { try { Directory.Delete(_lockDir, true); } catch { } }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl)
        : HttpMessageHandler
    {
        public int Concurrent;
        public int MaxConcurrent;
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var now = Interlocked.Increment(ref Concurrent);
            InterlockedMax(ref MaxConcurrent, now);
            try { return await impl(request, ct); }
            finally { Interlocked.Decrement(ref Concurrent); }
        }
        private static void InterlockedMax(ref int target, int value)
        {
            int snapshot;
            while (value > (snapshot = Volatile.Read(ref target)))
                Interlocked.CompareExchange(ref target, value, snapshot);
        }
    }

    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    private TallyClient NewClient(FakeHandler handler, Action<TallySettings>? tune = null)
    {
        var settings = new TallySettings { RequestTimeoutSeconds = 10, GateWaitSeconds = 5 };
        tune?.Invoke(settings);
        var client = new TallyClient(settings, NullLogger<TallyClient>.Instance,
            new HttpClient(handler), _lockDir)
        { DelayAsync = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        return client;
    }

    [Fact]
    public async Task ManyConcurrentTasks_ProduceAtMostOneInFlightTallyRequest()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(30, ct); // hold each request briefly
            return Xml("<ENVELOPE><OK/></ENVELOPE>");
        });
        using var client = NewClient(handler);

        await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => client.PostAsync("<ENVELOPE/>", CancellationToken.None)));

        Assert.Equal(12, handler.Calls);
        Assert.Equal(1, handler.MaxConcurrent);              // §D: single-flight
    }

    [Fact]
    public async Task Timeout_ReleasesGate_AndBudgetBoundsRetries()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);  // never responds
            throw new UnreachableException();
        });
        using var client = NewClient(handler, s => s.RequestTimeoutSeconds = 10);
        client.ResetRunBudget(2);                            // per-run budget: 2 retries

        // Ladder allows up to 3 retries, but the run budget stops it at 2:
        // attempts = 1 initial + 2 budgeted retries = 3 calls, then exhaustion.
        var ex = await Assert.ThrowsAsync<TallyException>(() =>
            client.PostAsync("<ENVELOPE/>", TimeSpan.FromMilliseconds(50), 3, CancellationToken.None));
        Assert.Equal(ErrorCategory.TallyTimeout, ex.Category);
        Assert.Contains("budget exhausted", ex.Message);
        Assert.Equal(3, handler.Calls);

        // Gate released: a healthy follow-up request succeeds immediately.
        var healthy = new FakeHandler((_, _) => Task.FromResult(Xml("<ENVELOPE><OK/></ENVELOPE>")));
        using var client2 = NewClient(healthy);
        Assert.NotNull(await client2.PostAsync("<ENVELOPE/>", CancellationToken.None));
    }

    [Fact]
    public async Task TransientHttpFailure_ReleasesGate_AndRecoversViaProbeFirstReconnect()
    {
        // A real loopback listener lets the probe-first reconnect succeed
        // deterministically offline (no external network).
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoop(listener);
        try
        {
            var calls = 0;
            var handler = new FakeHandler((_, _) =>
                Interlocked.Increment(ref calls) == 1
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        { Content = new StringContent("boom") })
                    : Task.FromResult(Xml("<ENVELOPE><OK/></ENVELOPE>")));
            using var client = NewClient(handler, s => { s.Host = "127.0.0.1"; s.Port = port; });

            // HTTP 500 → transient path → probe (listener answers) → single re-send → OK.
            var doc = await client.PostAsync("<ENVELOPE/>", CancellationToken.None);
            Assert.NotNull(doc);
            Assert.Equal(2, handler.Calls);

            // Gate is free afterwards: an immediate follow-up succeeds.
            Assert.NotNull(await client.PostAsync("<ENVELOPE/>", CancellationToken.None));
        }
        finally { listener.Stop(); }

        static async Task AcceptLoop(System.Net.Sockets.TcpListener l)
        {
            try { while (true) (await l.AcceptTcpClientAsync()).Dispose(); }
            catch { /* listener stopped */ }
        }
    }

    [Fact]
    public async Task Cancellation_ReleasesGate_AndIsNeverRetried()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new UnreachableException();
        });
        using var client = NewClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.PostAsync("<ENVELOPE/>", cts.Token));
        Assert.Equal(1, handler.Calls);                      // §F6: no retry after cancel

        var healthy = new FakeHandler((_, _) => Task.FromResult(Xml("<ENVELOPE><OK/></ENVELOPE>")));
        using var client2 = NewClient(healthy);
        Assert.NotNull(await client2.PostAsync("<ENVELOPE/>", CancellationToken.None));
    }

    [Fact]
    public async Task GateWaitTimeout_FailsWithTallyBusy_WithoutSendingRequest()
    {
        // Hold the CROSS-PROCESS lock externally so the client cannot acquire it.
        var lockPath = Path.Combine(_lockDir, "tally-gate.lock");
        using var external = new FileStream(lockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        var handler = new FakeHandler((_, _) => Task.FromResult(Xml("<ENVELOPE/>")));
        using var client = NewClient(handler, s => s.GateWaitSeconds = 5);
        // Injected DelayAsync is instant, so the bounded wait elapses via deadline.
        var ex = await Assert.ThrowsAsync<TallyException>(() =>
            client.PostAsync("<ENVELOPE/>", CancellationToken.None));
        Assert.Equal(ErrorCategory.TallyBusy, ex.Category);
        Assert.Equal(0, handler.Calls);                      // request was NOT sent
    }

    [Fact]
    public async Task OversizedResponse_FailsNonRetryably()
    {
        var big = new string('x', 64);
        var handler = new FakeHandler((_, _) =>
        {
            var resp = Xml("<ENVELOPE>" + big + "</ENVELOPE>");
            resp.Content.Headers.ContentLength = 2L * 1024 * 1024 * 1024; // 2 GB claim
            return Task.FromResult(resp);
        });
        using var client = NewClient(handler);
        var ex = await Assert.ThrowsAsync<TallyException>(() =>
            client.PostAsync("<ENVELOPE/>", CancellationToken.None));
        Assert.Equal(ErrorCategory.TallyResponseTooLarge, ex.Category);
    }

    private sealed class UnreachableException : Exception;
}
