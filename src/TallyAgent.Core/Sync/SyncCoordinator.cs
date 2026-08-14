using System.Text.Json;

namespace TallyAgent.Core.Sync;

public sealed record SyncLeaseInfo(string RunId, string Kind, int ProcessId, string AcquiredUtc);

/// <summary>Outcome of a coordination attempt. When not acquired, ActiveRun
/// identifies the run that holds the lease (safe metadata only — never
/// credentials or payloads).</summary>
public sealed record SyncAcquireResult(bool Acquired, SyncLeaseInfo? ActiveRun)
{
    public const string AlreadyRunning = "sync_already_running";
}

/// <summary>
/// THE process-wide AND machine-wide exclusion for top-level synchronization
/// (Phase C). Scheduled sync, Sync Now, Force Full Sync, Retry Failed Batches
/// (queue-mutating path) and startup recovery must all acquire a lease before
/// touching extraction state.
///
/// Mechanism: an exclusive lock FILE (FileShare.None) under ProgramData\locks.
/// The OS releases the handle when the holding process exits for ANY reason —
/// a crash can never strand the lock (§C8). A sidecar metadata file (world-
/// readable) identifies the active run so a second caller can report
/// `sync_already_running` with the active run ID (§C3). Works for the
/// service, a mistakenly console-launched second service instance, and any
/// future tooling — not just a disabled UI button (§C4/C5/C6).
///
/// Acquisition is bounded and cancellation-aware (§C7): waiters poll at a
/// fixed short interval up to the caller's timeout.
/// </summary>
public sealed class SyncCoordinator : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _lockPath;
    private readonly string _metaPath;
    private readonly SemaphoreSlim _local = new(1, 1);
    private FileStream? _held;
    private SyncLeaseInfo? _heldInfo;

    /// <summary>Injectable delay for deterministic tests.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    public SyncCoordinator(string? lockDirOverride = null)
    {
        var dir = lockDirOverride ?? Path.Combine(AgentInfo.DataDir, "locks");
        Directory.CreateDirectory(dir);
        _lockPath = Path.Combine(dir, "sync-run.lock");
        _metaPath = Path.Combine(dir, "sync-run.meta.json");
    }

    /// <summary>Try to become the single active sync run. Bounded wait;
    /// TimeSpan.Zero = fail fast (the normal service behaviour: a second
    /// request must NOT start another extraction, §C3).</summary>
    public async Task<SyncAcquireResult> TryAcquireAsync(string kind, string runId,
        TimeSpan maxWait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // In-process first (cheap): protects against any future second
            // invoker inside one process without relying on worker structure.
            if (await _local.WaitAsync(TimeSpan.Zero, ct))
            {
                try
                {
                    var stream = new FileStream(_lockPath, FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                    _held = stream;
                    _heldInfo = new SyncLeaseInfo(runId, kind, Environment.ProcessId,
                        DateTime.UtcNow.ToString("O"));
                    WriteMeta(_heldInfo);
                    return new SyncAcquireResult(true, _heldInfo);
                }
                catch (IOException)
                {
                    _local.Release(); // another PROCESS holds the machine lock
                }
            }

            if (DateTime.UtcNow >= deadline)
                return new SyncAcquireResult(false, ReadActiveMeta());

            await DelayAsync(PollInterval, ct);
        }
    }

    /// <summary>Release the lease. Safe to call once per successful acquire;
    /// the metadata file is left behind (stale metadata is harmless — the lock
    /// file is the authority and dies with the process).</summary>
    public void Release()
    {
        _held?.Dispose();
        _held = null;
        _heldInfo = null;
        _local.Release();
    }

    /// <summary>Who currently appears to hold the lease (advisory, for
    /// operator display). Null when nobody does or metadata is unreadable.</summary>
    public SyncLeaseInfo? ReadActiveMeta()
    {
        try
        {
            if (!File.Exists(_metaPath)) return null;
            using var fs = new FileStream(_metaPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<SyncLeaseInfo>(fs);
        }
        catch
        {
            return null;
        }
    }

    private void WriteMeta(SyncLeaseInfo info)
    {
        try
        {
            using var fs = new FileStream(_metaPath, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            JsonSerializer.Serialize(fs, info);
        }
        catch
        {
            // Advisory only — never block a sync because metadata couldn't be written.
        }
    }

    public void Dispose()
    {
        _held?.Dispose();
        _local.Dispose();
    }
}
