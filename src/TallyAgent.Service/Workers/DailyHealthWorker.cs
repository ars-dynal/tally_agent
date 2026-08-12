using TallyAgent.Core;
using TallyAgent.Core.Cloud;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Diagnostics;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Security;

namespace TallyAgent.Service.Workers;

/// <summary>
/// Emits one health summary per server-local day. Direct webhooks work without
/// the cloud notification service; the cloud /errors endpoint is also called so
/// it can fan out email to notifications.adminEmail when deployed.
/// </summary>
public sealed class DailyHealthWorker(
    AgentConfig config,
    IngestionApiClient api,
    BatchQueueRepository queue,
    CheckpointRepository checkpoints,
    ErrorLogRepository errorLog,
    WebhookNotifier webhooks,
    AgentState state,
    ILogger<DailyHealthWorker> log) : BackgroundService
{
    private DateOnly? _lastSentLocalDate;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.Notifications.EnableDailyHealthSummary)
        {
            log.LogInformation("Daily health summary disabled");
            return;
        }

        var hour = Math.Clamp(config.Notifications.DailyHealthHourLocal, 0, 23);
        log.LogInformation("DailyHealthWorker started (daily at {Hour}:00 server local time)", hour);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            if (now.Hour >= hour && _lastSentLocalDate != today)
            {
                await SendSummaryAsync(ct);
                _lastSentLocalDate = today;
            }

            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendSummaryAsync(CancellationToken ct)
    {
        QueueStats stats;
        try { stats = queue.GetStats(); }
        catch { stats = new QueueStats(-1, -1, -1, -1); }

        string? lastSync = null;
        string? lastError = null;
        try { lastSync = checkpoints.GetLastSuccessfulSyncUtc(); } catch { }
        try { lastError = errorLog.LastErrorMessage(); } catch { }

        var health = stats.Failed > 0 || !state.TallyConnected || state.CloudDegraded
            ? "DEGRADED" : "HEALTHY";

        var body =
            $"Status: {health}\n" +
            $"Agent: {config.Cloud.AgentId} v{AgentInfo.Version}\n" +
            $"Company: {config.Tally.Company}\n" +
            $"Machine: {Environment.MachineName}\n" +
            $"Tally connected: {state.TallyConnected}\n" +
            $"Tally company open: {state.TallyCompanyOpen}\n" +
            $"Cloud notification channel: {(state.CloudDegraded ? "Degraded" : "Available")}\n" +
            $"Current operation: {state.CurrentOperation}\n" +
            $"Last successful sync: {lastSync ?? "none"}\n" +
            $"Pending batches: {stats.Pending}\n" +
            $"Failed batches: {stats.Failed}\n" +
            $"Disk free MB: {SystemInfo.DiskFreeMb()}\n" +
            $"Agent memory MB: {SystemInfo.ProcessMemoryMb()}\n" +
            $"Latest error: {SecretMasker.Scrub(lastError ?? "none")}\n" +
            $"Generated local: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}";

        // Always send configured direct channels; this remains useful even when
        // Cloud Run's notification endpoint is not yet deployed.
        await webhooks.SendAsync("Tally Agent Daily Health", body, ct);

        var report = new ErrorReportRequest
        {
            AgentId = config.Cloud.AgentId,
            CompanyId = config.Cloud.CompanyId,
            MachineName = Environment.MachineName,
            CompanyName = config.Tally.Company,
            Category = "DailyHealth",
            Severity = health == "HEALTHY" ? "info" : "warning",
            Message = body,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Operation = state.CurrentOperation,
            AgentVersion = AgentInfo.Version,
            IsSummary = true,
            RecipientEmail = config.Notifications.EnableEmailAlerts
                && !string.IsNullOrWhiteSpace(config.Notifications.AdminEmail)
                    ? config.Notifications.AdminEmail : null,
        };

        try
        {
            await api.ReportErrorAsync(report, ct);
            state.CloudDegraded = false;
            log.LogInformation("Daily health summary delivered to cloud notification API");
        }
        catch (CloudApiException ex)
        {
            state.CloudDegraded = true;
            log.LogWarning("Daily health cloud delivery unavailable: {Msg}", SecretMasker.Scrub(ex.Message));
        }
        catch (Exception ex)
        {
            state.CloudDegraded = true;
            log.LogWarning("Daily health cloud delivery failed: {Msg}", SecretMasker.Scrub(ex.Message));
        }
    }
}
