using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Notifications;
using TallyAgent.Core.Security;

namespace TallyAgent.Core.Cloud;

public sealed class CloudApiException(ErrorCategory category, string message, bool retryable,
    TimeSpan? retryAfter = null, Exception? inner = null) : Exception(message, inner)
{
    public ErrorCategory Category { get; } = category;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// HTTPS client for the cloud ingestion API. Bearer-token auth, gzip NDJSON
/// batch upload, heartbeat, error reporting, update check. TLS certificate
/// validation is ALWAYS on. Secrets never appear in logs.
/// </summary>
public sealed class IngestionApiClient
{
    private readonly HttpClient _http;
    private readonly AgentConfig _config;
    private readonly ILogger<IngestionApiClient> _log;

    public IngestionApiClient(AgentConfig config, ILogger<IngestionApiClient> log, HttpClient? http = null)
    {
        _config = config;
        _log = log;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
        });
        _http.BaseAddress = new Uri(config.Cloud.IngestionApiUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ConfigStore.GetApiToken(config));
        _http.DefaultRequestHeaders.Add("X-Agent-Id", config.Cloud.AgentId);
        _http.DefaultRequestHeaders.Add("X-Environment", config.Cloud.Environment);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"TallyBigQueryAgent/{AgentInfo.Version}");
    }

    public async Task<PingResponse> PingAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "v1/ping"), ct);
        EnsureAuth(resp);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<PingResponse>(ct)
               ?? new PingResponse { Ok = false };
    }

    /// <summary>Upload one queued batch (payload already gzip NDJSON on disk).</summary>
    public async Task<BatchResponse> UploadBatchAsync(QueuedBatch batch, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "v1/batches");
            var stream = File.OpenRead(batch.PayloadPath); // disposed with request content
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            content.Headers.ContentEncoding.Add("gzip");
            req.Content = content;
            req.Headers.Add("X-Batch-Id", batch.BatchId);
            req.Headers.Add("X-Dataset", batch.Dataset);
            req.Headers.Add("X-Company", Uri.EscapeDataString(batch.Company));
            req.Headers.Add("X-Company-Id", _config.Cloud.CompanyId);
            req.Headers.Add("X-Sequence", batch.SequenceNo.ToString());
            req.Headers.Add("X-Sync-Id", batch.SyncId);
            req.Headers.Add("X-Record-Count", batch.RecordCount.ToString());
            req.Headers.Add("X-Checksum-Sha256", batch.ChecksumSha256);
            req.Headers.Add("X-Schema-Version", batch.SchemaVersion);
            req.Headers.Add("X-Agent-Version", AgentInfo.Version);
            req.Headers.Add("X-Extract-Start", batch.ExtractStartUtc);
            req.Headers.Add("X-Extract-End", batch.ExtractEndUtc);
            req.Headers.Add("X-Retry-Count", batch.RetryCount.ToString());
            if (batch.WindowFrom is not null) req.Headers.Add("X-Window-From", batch.WindowFrom);
            if (batch.WindowTo is not null) req.Headers.Add("X-Window-To", batch.WindowTo);
            return req;
        }, ct);

        EnsureAuth(resp);

        var body = await resp.Content.ReadAsStringAsync(ct);
        switch ((int)resp.StatusCode)
        {
            case 200 or 201 or 202:
                return TryParse(body) ?? new BatchResponse { Status = "accepted", BatchId = batch.BatchId };
            case 409:
                _log.LogInformation("Batch {BatchId} already ingested (409 duplicate)", batch.BatchId);
                return new BatchResponse { Status = "duplicate", BatchId = batch.BatchId };
            case 400 or 422:
                throw new CloudApiException(ErrorCategory.SchemaMismatch,
                    $"Ingestion API rejected batch {batch.BatchId}: {Truncate(body)}", retryable: false);
            case 413:
                throw new CloudApiException(ErrorCategory.UploadFailure,
                    $"Batch {batch.BatchId} too large ({batch.PayloadBytes} bytes). " +
                    "Reduce cloud.uploadBatchMaxRecords.", retryable: false);
            case 429:
                throw new CloudApiException(ErrorCategory.CloudApiUnavailable,
                    "Ingestion API rate limited (429).", retryable: true, RetryAfterOf(resp));
            default:
                throw new CloudApiException(ErrorCategory.CloudApiUnavailable,
                    $"Ingestion API returned HTTP {(int)resp.StatusCode}: {Truncate(body)}",
                    retryable: true, RetryAfterOf(resp));
        }
    }

    public async Task<HeartbeatResponse> SendHeartbeatAsync(HeartbeatRequest hb, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "v1/heartbeat")
        { Content = JsonContent.Create(hb) }, ct);
        EnsureAuth(resp);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<HeartbeatResponse>(ct)
               ?? new HeartbeatResponse { Ok = true };
    }

    public async Task ReportErrorAsync(ErrorReportRequest report, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "v1/errors")
        { Content = JsonContent.Create(report) }, ct);
        EnsureAuth(resp);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"v1/updates/check?current={AgentInfo.Version}&channel={_config.Cloud.Environment.ToLowerInvariant()}"), ct);
        if (resp.StatusCode == HttpStatusCode.NoContent || resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        EnsureAuth(resp);
        resp.EnsureSuccessStatusCode();
        var info = await resp.Content.ReadFromJsonAsync<UpdateInfo>(ct);
        return string.IsNullOrEmpty(info?.Version) ? null : info;
    }

    // ── plumbing ──────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        try
        {
            using var req = requestFactory();
            return await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudApiException(ErrorCategory.CloudApiUnavailable,
                "Ingestion API request timed out.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            var category = IsDnsOrOffline(ex)
                ? ErrorCategory.InternetUnavailable : ErrorCategory.CloudApiUnavailable;
            throw new CloudApiException(category,
                SecretMasker.Scrub($"Cannot reach ingestion API: {ex.Message}"), retryable: true, inner: ex);
        }
    }

    private static void EnsureAuth(HttpResponseMessage resp)
    {
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CloudApiException(ErrorCategory.AuthenticationFailure,
                $"Ingestion API rejected the agent token (HTTP {(int)resp.StatusCode}). " +
                "Verify the API token, agent ID and environment.", retryable: false);
    }

    private static bool IsDnsOrOffline(HttpRequestException ex) =>
        ex.InnerException is System.Net.Sockets.SocketException
        {
            SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound
                or System.Net.Sockets.SocketError.NoData
                or System.Net.Sockets.SocketError.NetworkUnreachable
                or System.Net.Sockets.SocketError.HostUnreachable
        };

    private static TimeSpan? RetryAfterOf(HttpResponseMessage resp) =>
        resp.Headers.RetryAfter?.Delta ??
        (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);

    private static BatchResponse? TryParse(string body)
    {
        try { return JsonSerializer.Deserialize<BatchResponse>(body); }
        catch { return null; }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
