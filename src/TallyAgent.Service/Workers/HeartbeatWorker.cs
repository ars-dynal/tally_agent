using System.Text.Json;
using TallyAgent.Core;
using TallyAgent.Core.Cloud;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Diagnostics;

namespace TallyAgent.Service.Workers;

/// <summary>
/// Sends the full health heartbeat every heartbeatMinutes (default 5).
/// Offline heartbeats are buffered in SQLite so the dashboard's history stays
/// complete. Server responses may carry commands (sync_now) which are honoured
/// via the trigger-file mechanism.
/// </summary>
public sealed class HeartbeatWorker(
    AgentConfig config,
    IngestionApiClient api,
    BatchQueueRepository queue,
    CheckpointRepository checkpoints,
    ErrorLogRepository errorLog,
    HeartbeatRepository heartbeats,
    AgentState state,
    ILogger<HeartbeatWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, config.Cloud.HeartbeatMinutes));
        log.LogInformation("HeartbeatWorker started (every {Interval})", interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var hb = Build();
                var json = JsonSerializer.Serialize(hb);
                long rowId;
                try { rowId = heartbeats.Insert(json, delivered: false); }
                catch { rowId = -1; }

                try
                {
                    var resp = await api.SendHeartbeatAsync(hb, ct);
                    state.InternetConnected = true;
                    if (rowId > 0) heartbeats.MarkDelivered(rowId);

                    foreach (var cmd in resp.Commands ?? [])
                        HandleCommand(cmd);
                }
                catch (CloudApiException ex)
                {
                    state.InternetConnected = ex.Category != Core.Notifications.ErrorCategory.InternetUnavailable;
                    log.LogWarning("Heartbeat not delivered ({Category}): {Msg}", ex.Category, ex.Message);
                }

                heartbeats.Purge();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.LogError(ex, "Heartbeat cycle failed");
            }
        } while (await SafeWait(timer, ct));
    }

    private HeartbeatRequest Build()
    {
        QueueStats stats;
        try { stats = queue.GetStats(); }
        catch { stats = new QueueStats(-1, -1, -1, -1); }

        string? lastError = null;
        try { lastError = errorLog.LastErrorMessage(); } catch { }

        string? lastSync = null;
        try { lastSync = checkpoints.GetLastSuccessfulSyncUtc(); } catch { }

        return new HeartbeatRequest
        {
            AgentId = config.Cloud.AgentId,
            CompanyId = config.Cloud.CompanyId,
            MachineName = Environment.MachineName,
            WindowsVersion = SystemInfo.WindowsVersion(),
            AgentVersion = AgentInfo.Version,
            Environment = config.Cloud.Environment,
            ServiceStatus = "running",
            TallyConnected = state.TallyConnected,
            TallyCompanyOpen = state.TallyCompanyOpen,
            TallyCompany = config.Tally.Company,
            LastSuccessfulSyncUtc = lastSync,
            LastAttemptedSyncUtc = state.LastAttemptedSyncUtc,
            CurrentOperation = state.CurrentOperation,
            PendingBatches = stats.Pending,
            FailedBatches = stats.Failed,
            LastError = lastError,
            DiskFreeMb = SystemInfo.DiskFreeMb(),
            MemoryUsedMb = SystemInfo.ProcessMemoryMb(),
            InternetConnected = SystemInfo.NetworkAvailable(),
            TimestampUtc = DateTime.UtcNow.ToString("O"),
        };
    }

    private void HandleCommand(AgentCommand cmd)
    {
        switch (cmd.Type)
        {
            case "sync_now":
                log.LogInformation("Server requested sync_now");
                try
                {
                    File.WriteAllText(Path.Combine(AgentInfo.TriggerDir, "sync-now.trigger"),
                        DateTime.UtcNow.ToString("O"));
                }
                catch (Exception ex) { log.LogWarning(ex, "Could not write sync trigger"); }
                break;
            case "update":
                log.LogInformation("Server offers update to {Version} — recorded for the updater",
                    cmd.Version);
                break;
            default:
                log.LogWarning("Unknown server command '{Type}' ignored", cmd.Type);
                break;
        }
    }

    private static async Task<bool> SafeWait(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
