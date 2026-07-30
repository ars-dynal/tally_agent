using Microsoft.Extensions.Logging;
using TallyAgent.Core.Cloud;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Security;

namespace TallyAgent.Core.Notifications;

/// <summary>
/// Dispatch policy:
///   • critical errors → sent immediately (cloud /v1/errors + direct webhooks),
///     throttled per group by CriticalAlertCooldownMinutes so a flapping failure
///     can't spam the admin;
///   • non-critical errors → logged locally, grouped, and sent as a periodic
///     summary by ErrorSummaryWorker.
/// All outgoing text passes through SecretMasker.
/// </summary>
public sealed class ErrorReporter(
    AgentConfig config,
    ErrorLogRepository errorLog,
    IngestionApiClient api,
    WebhookNotifier webhooks,
    ILogger<ErrorReporter> log)
{
    private readonly Dictionary<string, DateTime> _lastCriticalSent = [];
    private readonly object _lock = new();

    /// <summary>Log the error locally and, when critical, alert immediately.</summary>
    public async Task ReportAsync(ErrorCategory category, ErrorSeverity severity, string message,
        string? stackTrace = null, string? operation = null, string? dataset = null,
        string? batchId = null, int retryCount = 0, CancellationToken ct = default)
    {
        message = SecretMasker.Scrub(message);
        stackTrace = stackTrace is null ? null : SecretMasker.Scrub(stackTrace);

        long id;
        try
        {
            id = errorLog.Insert(category, severity, message, stackTrace, operation, dataset, batchId, retryCount);
        }
        catch (Exception dbEx)
        {
            log.LogCritical(dbEx, "Local database failure while logging an error");
            return;
        }

        if (severity != ErrorSeverity.Critical) return;

        var groupKey = $"{category}:{dataset}";
        lock (_lock)
        {
            if (_lastCriticalSent.TryGetValue(groupKey, out var last) &&
                DateTime.UtcNow - last < TimeSpan.FromMinutes(config.Notifications.CriticalAlertCooldownMinutes))
            {
                log.LogInformation("Critical alert for {Group} suppressed (cooldown)", groupKey);
                return;
            }
            _lastCriticalSent[groupKey] = DateTime.UtcNow;
        }

        var report = BuildReport(category, severity, message, stackTrace, operation, dataset, batchId, retryCount);
        var delivered = false;

        // Primary: cloud API (unless the cloud/auth itself is what failed)
        if (category is not (ErrorCategory.CloudApiUnavailable or ErrorCategory.InternetUnavailable
            or ErrorCategory.AuthenticationFailure))
        {
            try { await api.ReportErrorAsync(report, ct); delivered = true; }
            catch (Exception ex) { log.LogWarning("Cloud error report failed: {Msg}", SecretMasker.Scrub(ex.Message)); }
        }

        // Fallback / parallel: direct webhooks
        await webhooks.SendAsync(
            $"Tally Agent Alert — {category}",
            FormatAlertBody(report), ct);

        if (delivered) errorLog.MarkReported([id]);
    }

    /// <summary>Send one grouped summary (called by ErrorSummaryWorker).</summary>
    public async Task SendSummariesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<(string GroupKey, string Category, string? Dataset, long Count, string LastMessage, List<long> Ids)> groups;
        try { groups = errorLog.GetUnreportedGroups(); }
        catch (Exception ex) { log.LogError(ex, "Cannot read error groups"); return; }

        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var report = new ErrorReportRequest
            {
                AgentId = config.Cloud.AgentId,
                CompanyId = config.Cloud.CompanyId,
                MachineName = Environment.MachineName,
                CompanyName = config.Tally.Company,
                Category = g.Category,
                Severity = "error",
                Message = SecretMasker.Scrub(
                    $"{g.Category}{(g.Dataset is null ? "" : $" [{g.Dataset}]")} occurred {g.Count}× " +
                    $"since last summary. Latest: {g.LastMessage}"),
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                Dataset = g.Dataset,
                AgentVersion = AgentInfo.Version,
                IsSummary = true,
                Occurrences = g.Count,
            };
            try
            {
                await api.ReportErrorAsync(report, ct);
                errorLog.MarkReported(g.Ids);
            }
            catch (Exception ex)
            {
                log.LogWarning("Error summary for {Group} not delivered: {Msg}",
                    g.GroupKey, SecretMasker.Scrub(ex.Message));
                return; // cloud unreachable — keep groups unreported, retry next interval
            }
        }
    }

    private ErrorReportRequest BuildReport(ErrorCategory category, ErrorSeverity severity,
        string message, string? stackTrace, string? operation, string? dataset,
        string? batchId, int retryCount) => new()
    {
        AgentId = config.Cloud.AgentId,
        CompanyId = config.Cloud.CompanyId,
        MachineName = Environment.MachineName,
        CompanyName = config.Tally.Company,
        Category = category.ToString(),
        Severity = severity.ToString().ToLowerInvariant(),
        Message = message,
        StackTrace = stackTrace,
        TimestampUtc = DateTime.UtcNow.ToString("O"),
        Operation = operation,
        Dataset = dataset,
        BatchId = batchId,
        RetryCount = retryCount,
        AgentVersion = AgentInfo.Version,
    };

    private string FormatAlertBody(ErrorReportRequest r) =>
        $"Agent: {r.AgentId}\n" +
        $"Company: {r.CompanyName}\n" +
        $"Status: Failed\n" +
        $"Issue: {r.Message}\n" +
        $"Machine: {r.MachineName}\n" +
        $"Environment: {config.Cloud.Environment}\n" +
        $"Retry attempt: {r.RetryCount}\n" +
        $"Agent version: {r.AgentVersion}\n" +
        $"Time (UTC): {r.TimestampUtc}";
}
