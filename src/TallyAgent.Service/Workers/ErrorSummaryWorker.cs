using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Notifications;

namespace TallyAgent.Service.Workers;

/// <summary>
/// Periodically (default hourly) sends grouped summaries of non-critical errors
/// instead of a notification per occurrence, and purges expired error rows.
/// Critical errors bypass this worker entirely (ErrorReporter sends them
/// immediately with a per-group cooldown).
/// </summary>
public sealed class ErrorSummaryWorker(
    AgentConfig config,
    ErrorReporter reporter,
    ErrorLogRepository errorLog,
    ILogger<ErrorSummaryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(10, config.Notifications.SummaryIntervalMinutes));
        log.LogInformation("ErrorSummaryWorker started (every {Interval})", interval);

        using var timer = new PeriodicTimer(interval);
        while (await SafeWait(timer, ct))
        {
            try
            {
                await reporter.SendSummariesAsync(ct);
                var purged = errorLog.Purge();
                if (purged > 0) log.LogInformation("Purged {N} expired error-log rows", purged);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.LogError(ex, "Error summary cycle failed");
            }
        }
    }

    private static async Task<bool> SafeWait(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
