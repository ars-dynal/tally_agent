using TallyAgent.Core;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Sync;
using TallyAgent.Core.Tally;

namespace TallyAgent.Service.Workers;

/// <summary>
/// Production extraction loop. A fresh installation automatically performs a
/// FULL sync. Incremental cycles are blocked until that FULL session has been
/// extracted and every queued batch has been acknowledged by the cloud API.
/// </summary>
public sealed class SyncWorker(
    AgentConfig config,
    SyncEngine engine,
    TallyClient tally,
    ErrorReporter errors,
    CheckpointRepository checkpoints,
    AgentDatabase db,
    AgentState state,
    ILogger<SyncWorker> log) : BackgroundService
{
    private static string SyncTriggerPath => Path.Combine(AgentInfo.TriggerDir, "sync-now.trigger");
    private static string ForceFullTriggerPath => Path.Combine(AgentInfo.TriggerDir, "force-full.trigger");
    private readonly SyncSessionRepository _sessions = new(db);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("SyncWorker started: every {N} min, lookback {L} days",
            config.Tally.SyncFrequencyMinutes, config.Tally.IncrementalLookbackDays);

        await SafeDelay(TimeSpan.FromSeconds(20), ct);

        var interval = TimeSpan.FromMinutes(config.Tally.SyncFrequencyMinutes);
        var nextRun = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var trigger = ConsumeTrigger();
            if (DateTime.UtcNow >= nextRun || trigger != TriggerKind.None)
            {
                nextRun = DateTime.UtcNow + interval;
                state.LastAttemptedSyncUtc = DateTime.UtcNow.ToString("O");
                try
                {
                    var probe = await tally.ProbeAsync(ct);
                    state.TallyConnected = probe.Ok || probe.Category == ErrorCategory.TallyCompanyNotOpen;
                    state.TallyCompanyOpen = probe.Ok;
                    if (!probe.Ok)
                        throw new TallyException(probe.Category ?? ErrorCategory.TallyNotRunning,
                            probe.Error ?? "Tally probe failed");

                    var company = ResolvedCompany(probe);
                    if (trigger == TriggerKind.ForceFull)
                    {
                        var removed = checkpoints.ResetForCompany(company);
                        log.LogWarning("Force Full Sync requested: reset {Count} checkpoints for {Company}; cloud data was not deleted",
                            removed, company);
                    }

                    var readyFull = _sessions.HasReadyFull(company);
                    var voucherCheckpoint = checkpoints.Get("_vouchers_window", company);
                    var latestSession = _sessions.GetLatest(company);

                    // Upgrade from pre-session versions: old checkpoints are test state
                    // and must not silently authorize incrementals in production.
                    if (!readyFull && voucherCheckpoint is { FullSyncDone: true } && latestSession is null)
                    {
                        checkpoints.ResetForCompany(company);
                        voucherCheckpoint = null;
                        log.LogWarning("Legacy checkpoint state detected without a production FULL session; starting a clean FULL sync");
                    }

                    // Extraction is complete but uploads are still draining. Do not
                    // create another cycle or move to incremental prematurely.
                    if (!readyFull && voucherCheckpoint is { FullSyncDone: true } &&
                        latestSession is { Status: SyncSessionRepository.Uploading or SyncSessionRepository.Extracting })
                    {
                        state.CurrentOperation = $"waiting for full upload ({latestSession.SyncId})";
                        log.LogInformation(
                            "FULL session {SyncId} is {Status}: {Acked}/{Queued} batches acknowledged; incremental remains blocked",
                            latestSession.SyncId, latestSession.Status,
                            latestSession.BatchesAcknowledged, latestSession.BatchesQueued);
                        await SafeDelay(TimeSpan.FromSeconds(5), ct);
                        continue;
                    }

                    var mode = readyFull ? "incremental" : "full";
                    state.CurrentOperation = $"sync ({mode})";
                    var started = DateTime.UtcNow.ToString("O");
                    var result = await engine.RunCycleAsync(mode, ct);

                    _sessions.Register(result.SyncId, company, mode, started);
                    _sessions.MarkExtractionCompleted(
                        result.SyncId,
                        result.RowsExtracted,
                        result.DatasetsFailed,
                        result.Errors.Count > 0 ? string.Join("; ", result.Errors) : null);

                    if (result.Status == "failed" && result.Errors.Count > 0)
                        log.LogWarning("Sync cycle failed: {Errors}", string.Join("; ", result.Errors));
                    else
                        log.LogInformation(
                            "Production {Mode} session {SyncId} extraction finished; waiting for cloud acknowledgements",
                            mode, result.SyncId);
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
                }
            }

            await SafeDelay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private string ResolvedCompany(TallyProbeResult probe) =>
        !string.IsNullOrWhiteSpace(config.Tally.Company) ? config.Tally.Company
        : probe.Companies.Count > 0 ? probe.Companies[0]
        : throw new TallyException(ErrorCategory.TallyCompanyNotOpen, "No Tally company is open");

    private TriggerKind ConsumeTrigger()
    {
        try
        {
            if (File.Exists(ForceFullTriggerPath))
            {
                File.Delete(ForceFullTriggerPath);
                log.LogWarning("Force Full Sync trigger received");
                return TriggerKind.ForceFull;
            }
            if (File.Exists(SyncTriggerPath))
            {
                File.Delete(SyncTriggerPath);
                log.LogInformation("Manual sync trigger received");
                return TriggerKind.SyncNow;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Unable to consume sync trigger");
        }
        return TriggerKind.None;
    }

    private enum TriggerKind { None, SyncNow, ForceFull }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}
