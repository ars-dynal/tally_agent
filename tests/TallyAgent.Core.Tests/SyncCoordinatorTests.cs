using TallyAgent.Core.Sync;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>Phase C exclusion tests: one active run per machine, stable
/// sync_already_running result, crash-safe lock release.</summary>
public class SyncCoordinatorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "coord-tests-" + Guid.NewGuid().ToString("N"));

    public SyncCoordinatorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SyncCoordinator NewCoordinator() =>
        new(_dir) { DelayAsync = (_, _) => Task.CompletedTask };

    [Theory]
    [InlineData("manual", "manual")]          // two manual requests (§C9a)
    [InlineData("manual", "scheduled")]       // manual + scheduled (§C9b)
    [InlineData("manual", "full-forced")]     // manual + force-full (§C9c)
    [InlineData("scheduled", "retry-failed")] // scheduled + retry (§C9d)
    public async Task SecondRequest_WhileActive_ReturnsAlreadyRunning_WithActiveRunId(
        string firstKind, string secondKind)
    {
        using var a = NewCoordinator();
        using var b = NewCoordinator(); // separate instance = separate process semantics

        var first = await a.TryAcquireAsync(firstKind, "run-A", TimeSpan.Zero, CancellationToken.None);
        Assert.True(first.Acquired);

        var second = await b.TryAcquireAsync(secondKind, "run-B", TimeSpan.Zero, CancellationToken.None);
        Assert.False(second.Acquired);                       // no second extraction
        Assert.Equal("run-A", second.ActiveRun?.RunId);      // identifies active run
        Assert.Equal(firstKind, second.ActiveRun?.Kind);
        Assert.Equal("sync_already_running", SyncAcquireResult.AlreadyRunning);

        a.Release();
        var third = await b.TryAcquireAsync(secondKind, "run-B", TimeSpan.Zero, CancellationToken.None);
        Assert.True(third.Acquired);                         // released lease is reacquirable
        b.Release();
    }

    [Fact]
    public async Task DuplicateCommand_SameProcess_IsRefused()
    {
        using var c = NewCoordinator();
        Assert.True((await c.TryAcquireAsync("manual", "r1", TimeSpan.Zero, CancellationToken.None)).Acquired);
        // duplicate UI command lands on the in-process semaphore, not the file
        Assert.False((await c.TryAcquireAsync("manual", "r2", TimeSpan.Zero, CancellationToken.None)).Acquired);
        c.Release();
    }

    [Fact]
    public async Task ProcessCrash_DoesNotStrandTheLock()
    {
        var crashed = NewCoordinator();
        Assert.True((await crashed.TryAcquireAsync("manual", "dead-run", TimeSpan.Zero,
            CancellationToken.None)).Acquired);

        // Simulate the holding process dying WITHOUT calling Release():
        // disposing the coordinator disposes the FileStream, exactly what the
        // OS does to the handle when the process terminates.
        crashed.Dispose();

        using var restarted = NewCoordinator();
        var after = await restarted.TryAcquireAsync("scheduled", "restart-run",
            TimeSpan.Zero, CancellationToken.None);
        Assert.True(after.Acquired);                         // §C8: never stranded
        restarted.Release();
    }

    [Fact]
    public async Task ManyCallersRacing_ExactlyOneWins()
    {
        var winners = 0;
        var coordinators = Enumerable.Range(0, 8).Select(_ => NewCoordinator()).ToList();
        try
        {
            var tasks = coordinators.Select((c, i) => Task.Run(async () =>
            {
                var r = await c.TryAcquireAsync("race", $"run-{i}", TimeSpan.Zero, CancellationToken.None);
                if (r.Acquired) Interlocked.Increment(ref winners);
            }));
            await Task.WhenAll(tasks);
            Assert.Equal(1, winners);                        // §C9g
        }
        finally
        {
            foreach (var c in coordinators) c.Dispose();
        }
    }

    [Fact]
    public async Task BoundedWait_IsCancellationAware()
    {
        using var holder = NewCoordinator();
        Assert.True((await holder.TryAcquireAsync("manual", "r1", TimeSpan.Zero,
            CancellationToken.None)).Acquired);

        using var waiter = NewCoordinator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            waiter.TryAcquireAsync("manual", "r2", TimeSpan.FromMinutes(5), cts.Token));
        holder.Release();
    }
}
