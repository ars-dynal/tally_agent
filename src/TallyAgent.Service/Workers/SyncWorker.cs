using TallyAgent.Core;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Sync;
using TallyAgent.Core.Tally;

namespace TallyAgent.Service.Workers;

/// <summary>
/// Extraction loop: runs a sync cycle every syncFrequencyMinutes, plus
/// immediately when a "sync-now.trigger" file appears (written by the
/// manager app / CLI). Extraction is fully decoupled from upload.
/// </summary>
public sealed class SyncWorker(
    AgentConfig config,
    SyncEngine engine,
    SyncCoordinator coordinator,
    TallyClient tally,
    ErrorReporter errors,
    CheckpointRepository checkpoints,
    RunHistoryRepository runs,
    RunAlerter alerter,
    BatchQueueRepository queue,
    AgentState state,
    ILogger<SyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("SyncWorker started: every {N} min, lookback {L} days",
            config.Tally.SyncFrequencyMinutes, config.Tally.IncrementalLookbackDays);

        // Say once whether anybody would actually be told about a failure.
        alerter.LogChannelStatus();

        // Small startup delay so boot-time services (incl. Tally) settle first.
        await SafeDelay(TimeSpan.FromSeconds(20), ct);

        var interval = TimeSpan.FromMinutes(config.Tally.SyncFrequencyMinutes);
        var nextRun = DateTime.UtcNow; // first cycle immediately

        while (!ct.IsCancellationRequested)
        {
            await CheckForStallOrBacklogAsync(ct);

            var forceFull = ConsumeTrigger("force-full");
            var manual = ConsumeTrigger("sync-now") || forceFull;
            if (DateTime.UtcNow >= nextRun || manual)
            {
                // Provisional (in case the cycle crashes before the finally
                // below); the real next-run time is computed AFTER the cycle
                // ends so Tally always gets a full idle interval between cycles.
                nextRun = DateTime.UtcNow + interval;
                state.LastAttemptedSyncUtc = DateTime.UtcNow.ToString("O");
                try
                {
                    var probe = await tally.ProbeAsync(ct);
                    state.TallyConnected = probe.Ok || probe.Category
                        is Core.Notifications.ErrorCategory.TallyCompanyNotOpen
                        or Core.Notifications.ErrorCategory.TallyCompanyMismatch;
                    state.TallyCompanyOpen = probe.Ok;

                    // Say WHY the mode was chosen. Without this line, a walk
                    // that had completed 85/85 windows but never latched its
                    // checkpoint looked identical to one that had never run,
                    // and it took a code read to tell them apart.
                    var company = ResolvedCompany(probe);
                    var voucherCp = checkpoints.Get("_vouchers_window", company);
                    var mode = forceFull ? "full-forced"
                        : manual ? "manual"
                        : voucherCp is { FullSyncDone: true } ? "incremental" : "full";
                    log.LogInformation(
                        "Sync mode '{Mode}' for company '{Company}': FullSyncDone={Done}, " +
                        "frontier={Frontier}, covered-to={To}{Checkpoint}",
                        mode, company, voucherCp?.FullSyncDone,
                        voucherCp?.LastFromDate ?? "none", voucherCp?.LastToDate ?? "none",
                        voucherCp is null ? " (NO CHECKPOINT ROW - first run, or the company key does not match)" : "");

                    // Phase C: THE authoritative exclusion. Zero-wait — a second
                    // request while a run is active never starts extraction; it
                    // reports sync_already_running with the active run's id.
                    var runId = Guid.NewGuid().ToString("N")[..12];
                    var lease = await coordinator.TryAcquireAsync(mode, runId, TimeSpan.Zero, ct);
                    if (!lease.Acquired)
                    {
                        var active = lease.ActiveRun;
                        log.LogWarning("{Status}: run {ActiveId} ({Kind}) is active in process {Pid} — not starting another",
                            SyncAcquireResult.AlreadyRunning,
                            active?.RunId ?? "unknown", active?.Kind ?? "unknown", active?.ProcessId ?? 0);
                        state.CurrentOperation = $"{SyncAcquireResult.AlreadyRunning} ({active?.RunId ?? "unknown"})";
                        continue;
                    }

                    state.CurrentOperation = $"sync ({mode})";
                    SyncResult result;
                    try
                    {
                        result = await engine.RunCycleAsync(mode, probe, ct);
                    }
                    finally
                    {
                        coordinator.Release();
                    }

                    if (result.Status == "failed" && result.Errors.Count > 0)
                        log.LogWarning("Sync cycle failed: {Errors}", string.Join("; ", result.Errors));

                    // Tell somebody. Both September incidents were found by a
                    // person opening a console; nothing reached anyone.
                    var finished = runs.Latest();
                    if (finished is not null && finished.SyncId == result.SyncId)
                    {
                        if (finished.Status == "failed")
                            await alerter.RunFailedAsync(finished, CancellationToken.None);
                        else if (finished.Status == "partial" ||
                                 finished.DatasetsSucceeded < finished.DatasetsAttempted)
                            await alerter.RunIncompleteAsync(finished, CancellationToken.None);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.LogError(ex, "Sync cycle crashed");
                    await errors.ReportAsync(ErrorCategory.UnexpectedException, ErrorSeverity.Critical,
                        $"Sync cycle crashed: {ex.Message}", ex.StackTrace,
                        operation: engine.CurrentOperation, ct: CancellationToken.None);
                }
                finally
                {
                    state.CurrentOperation = "idle";
                    // v2.0.5: schedule from the END of the cycle, not the start.
                    // v2.0.4 computed nextRun before running, so any cycle longer
                    // than the interval was followed by another one immediately —
                    // Tally never got an idle gap during office hours.
                    nextRun = DateTime.UtcNow + interval;
                    log.LogInformation("Next scheduled sync at {Next:HH:mm:ss} UTC", nextRun);
                }
            }

            await SafeDelay(TimeSpan.FromSeconds(5), ct); // trigger poll granularity
        }
    }

    private string ResolvedCompany(TallyProbeResult probe) =>
        !string.IsNullOrWhiteSpace(config.Tally.Company) ? config.Tally.Company
        : probe.Companies.Count > 0 ? probe.Companies[0] : "";

    /// <summary>
    /// The two failures nobody was told about: a run that hangs with no
    /// progress, and a queue that stops draining. Both are invisible in a log
    /// nobody is reading.
    /// </summary>
    private async Task CheckForStallOrBacklogAsync(CancellationToken ct)
    {
        try
        {
            var idleLimit = Math.Max(5, config.Notifications.StalledAfterMinutes);
            var p = SyncProgressStore.Read();
            if (p is { Status: "running" } &&
                DateTime.TryParse(p.UpdatedUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var updated))
            {
                var idle = (int)(DateTime.UtcNow - updated.ToUniversalTime()).TotalMinutes;
                if (idle >= idleLimit)
                {
                    DateTime.TryParse(p.StartedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var started);
                    await alerter.RunStalledAsync(p.Operation, started, idle, ct);
                }
            }

            var stats = queue.GetStats();
            if (stats.Failed > 0 || stats.Pending > 50)
            {
                var oldest = queue.ListByStatus("pending", 1).FirstOrDefault()?.CreatedUtc;
                await alerter.QueueNotDrainingAsync(stats.Pending, stats.Failed, oldest, ct);
            }
        }
        catch (Exception ex) { log.LogDebug("Stall/backlog check skipped ({Msg})", ex.Message); }
    }

    private bool ConsumeTrigger(string name)
    {
        try
        {
            var path = Path.Combine(AgentInfo.TriggerDir, $"{name}.trigger");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            log.LogInformation("Trigger '{Name}' received", name);
            return true;
        }
        catch { return false; }
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}
