using TallyAgent.Core.Cloud;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Notifications;

namespace TallyAgent.Service.Workers;

/// <summary>
/// Drains the durable batch queue to the cloud ingestion API.
/// Successful acknowledgements are also recorded against the production sync
/// session, allowing FULL → INCREMENTAL promotion only after all batches arrive.
/// </summary>
public sealed class UploadWorker(
    AgentConfig config,
    BatchQueueRepository queue,
    IngestionApiClient api,
    ErrorReporter errors,
    AgentDatabase db,
    AgentState state,
    ILogger<UploadWorker> log) : BackgroundService
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AuthPause = TimeSpan.FromMinutes(10);
    private readonly SyncSessionRepository _sessions = new(db);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("UploadWorker started");
        await SafeDelay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            QueuedBatch? batch = null;
            try
            {
                batch = queue.DequeueNextDue(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Queue read failed");
                await errors.ReportAsync(ErrorCategory.LocalDatabaseFailure, ErrorSeverity.Critical,
                    $"Cannot read upload queue: {ex.Message}", ex.StackTrace, ct: CancellationToken.None);
                await SafeDelay(TimeSpan.FromMinutes(1), ct);
                continue;
            }

            if (batch is null)
            {
                await SafeDelay(IdlePoll, ct);
                continue;
            }

            if (!File.Exists(batch.PayloadPath))
            {
                log.LogError("Payload missing for batch {BatchId} — marking failed", batch.BatchId);
                queue.MarkFailed(batch.BatchId, "Payload file missing on disk");
                continue;
            }

            try
            {
                var resp = await api.UploadBatchAsync(batch, ct);
                state.InternetConnected = true;

                if (resp.Status is "accepted" or "duplicate")
                {
                    queue.Ack(batch.BatchId);
                    _sessions.RecordBatchAcknowledged(batch.SyncId, batch.BatchId);
                    log.LogInformation(
                        "Batch {BatchId} {Status} ({Records} records, session {SyncId}, attempt {Attempt})",
                        batch.BatchId, resp.Status, batch.RecordCount, batch.SyncId, batch.RetryCount + 1);
                }
                else
                {
                    queue.MarkFailed(batch.BatchId, $"Unexpected API status '{resp.Status}'");
                    await errors.ReportAsync(ErrorCategory.UploadFailure, ErrorSeverity.Error,
                        $"Batch {batch.BatchId}: unexpected ingestion status '{resp.Status}'",
                        dataset: batch.Dataset, batchId: batch.BatchId,
                        retryCount: batch.RetryCount, ct: CancellationToken.None);
                }
            }
            catch (CloudApiException ex) when (ex.Category == ErrorCategory.AuthenticationFailure)
            {
                queue.ScheduleRetry(batch.BatchId, ex.Message, AuthPause);
                await errors.ReportAsync(ex.Category, ErrorSeverity.Critical, ex.Message,
                    dataset: batch.Dataset, batchId: batch.BatchId,
                    retryCount: batch.RetryCount, ct: CancellationToken.None);
                log.LogError("Authentication failure — pausing uploads for {Pause}", AuthPause);
                await SafeDelay(AuthPause, ct);
            }
            catch (CloudApiException ex) when (!ex.Retryable)
            {
                queue.MarkFailed(batch.BatchId, ex.Message);
                await errors.ReportAsync(ex.Category, ErrorSeverity.Critical,
                    $"Batch {batch.BatchId} permanently rejected: {ex.Message}",
                    dataset: batch.Dataset, batchId: batch.BatchId,
                    retryCount: batch.RetryCount, ct: CancellationToken.None);
            }
            catch (CloudApiException ex)
            {
                state.InternetConnected = ex.Category != ErrorCategory.InternetUnavailable;
                var delay = ex.RetryAfter ??
                    RetryPolicy.NextDelay(batch.RetryCount, config.Advanced.MaxUploadRetryMinutes);
                queue.ScheduleRetry(batch.BatchId, ex.Message, delay);
                log.LogWarning("Batch {BatchId} upload failed ({Category}) — retry in {Delay:F0}s: {Msg}",
                    batch.BatchId, ex.Category, delay.TotalSeconds, ex.Message);

                await errors.ReportAsync(ex.Category, ErrorSeverity.Warning, ex.Message,
                    dataset: batch.Dataset, batchId: batch.BatchId,
                    retryCount: batch.RetryCount + 1, ct: CancellationToken.None);
                await SafeDelay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                queue.ScheduleRetry(batch.BatchId, ex.Message,
                    RetryPolicy.NextDelay(batch.RetryCount, config.Advanced.MaxUploadRetryMinutes));
                log.LogError(ex, "Unexpected upload error for {BatchId}", batch.BatchId);
                await errors.ReportAsync(ErrorCategory.UnexpectedException, ErrorSeverity.Error,
                    $"Upload of {batch.BatchId} crashed: {ex.Message}", ex.StackTrace,
                    dataset: batch.Dataset, batchId: batch.BatchId, ct: CancellationToken.None);
            }
        }
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}
