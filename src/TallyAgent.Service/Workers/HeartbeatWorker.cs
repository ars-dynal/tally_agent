using System.Text.Json;
using TallyAgent.Core;
using TallyAgent.Core.Cloud;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Diagnostics;

namespace TallyAgent.Service.Workers;

/// <summary>
/// Sends health heartbeats at the configured cadence. When the deployed cloud API
/// does not expose /heartbeat (HTTP 404), the worker verifies /health, marks the
/// monitoring channel degraded, and backs off heartbeat attempts to hourly so a
/// missing optional route cannot flood logs every five minutes.
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
    private DateTime _nextHeartbeatAttemptUtc = DateTime.MinValue;
    private bool _unsupportedLogged;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, config.Cloud.HeartbeatMinutes));
        log.LogInformation("HeartbeatWorker started (every {Interval}; unsupported endpoint backs off hourly)", interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            if (DateTime.UtcNow < _nextHeartbeatAttemptUtc)
                continue;

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
                    state.CloudDegraded = false;
                    _nextHeartbeatAttemptUtc = DateTime.UtcNow + interval;
                    _unsupportedLogged = false;
                    if (rowId > 0) heartbeats.MarkDelivered(rowId);

                    foreach (var cmd in resp.Commands ?? [])
                        HandleCommand(cmd);
                }
                catch (CloudApiException ex) when (IsMissingHeartbeatRoute(ex))
                {
                    state.CloudDegraded = true;

                    // Distinguish "monitoring route missing" from "cloud offline".
                    try
                    {
                        await api.PingAsync(ct);
                        state.InternetConnected = true;
                    }
                    catch
                    {
                        state.InternetConnected = false;
                    }

                    _nextHeartbeatAttemptUtc = DateTime.UtcNow.AddHours(1);
                    if (!_unsupportedLogged)
                    {
                        log.LogWarning(
                            "Cloud /health is reachable but /heartbeat is not deployed (HTTP 404). " +
                            "Agent remains operational; remote monitoring is DEGRADED. Retrying heartbeat hourly.");
                        _unsupportedLogged = true;
                    }
                }
                catch (CloudApiException ex)
                {
                    state.InternetConnected = ex.Category != Core.Notifications.ErrorCategory.InternetUnavailable;
                    state.CloudDegraded = true;
                    _nextHeartbeatAttemptUtc = DateTime.UtcNow + interval;
                    log.LogWarning("Heartbeat not delivered ({Category}): {Msg}", ex.Category, ex.Message);
                }

                heartbeats.Purge();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                state.CloudDegraded = true;
                log.LogWarning("Heartbeat cycle failed: {Message}", ex.Message);
            }
        } while (await SafeWait(timer, ct));
    }

    private static bool IsMissingHeartbeatRoute(CloudApiException ex) =>
        ex.Message.Contains("heartbeat returned HTTP 404", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase)
           && ex.Message.Contains("heartbeat", StringComparison.OrdinalIgnoreCase);

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
