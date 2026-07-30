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
    TallyClient tally,
    ErrorReporter errors,
    CheckpointRepository checkpoints,
    AgentState state,
    ILogger<SyncWorker> log) : BackgroundService
{
    private static string TriggerPath => Path.Combine(AgentInfo.TriggerDir, "sync-now.trigger");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("SyncWorker started: every {N} min, lookback {L} days",
            config.Tally.SyncFrequencyMinutes, config.Tally.IncrementalLookbackDays);

        // Small startup delay so boot-time services (incl. Tally) settle first.
        await SafeDelay(TimeSpan.FromSeconds(20), ct);

        var interval = TimeSpan.FromMinutes(config.Tally.SyncFrequencyMinutes);
        var nextRun = DateTime.UtcNow; // first cycle immediately

        while (!ct.IsCancellationRequested)
        {
            var manual = ConsumeTrigger();
            if (DateTime.UtcNow >= nextRun || manual)
            {
                nextRun = DateTime.UtcNow + interval;
                state.LastAttemptedSyncUtc = DateTime.UtcNow.ToString("O");
                try
                {
                    var probe = await tally.ProbeAsync(ct);
                    state.TallyConnected = probe.Ok || probe.Category == Core.Notifications.ErrorCategory.TallyCompanyNotOpen;
                    state.TallyCompanyOpen = probe.Ok;

                    var mode = manual ? "manual"
                        : checkpoints.Get("_vouchers_window", ResolvedCompany(probe)) is { FullSyncDone: true }
                            ? "incremental" : "full";
                    state.CurrentOperation = $"sync ({mode})";

                    var result = await engine.RunCycleAsync(mode, ct);

                    if (result.Status == "failed" && result.Errors.Count > 0)
                        log.LogWarning("Sync cycle failed: {Errors}", string.Join("; ", result.Errors));
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

            await SafeDelay(TimeSpan.FromSeconds(5), ct); // trigger poll granularity
        }
    }

    private string ResolvedCompany(TallyProbeResult probe) =>
        !string.IsNullOrWhiteSpace(config.Tally.Company) ? config.Tally.Company
        : probe.Companies.Count > 0 ? probe.Companies[0] : "";

    private bool ConsumeTrigger()
    {
        try
        {
            if (!File.Exists(TriggerPath)) return false;
            File.Delete(TriggerPath);
            log.LogInformation("Manual sync trigger received");
            return true;
        }
        catch { return false; }
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}
