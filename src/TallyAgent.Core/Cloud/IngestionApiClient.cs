using System.IO.Compression;
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
/// HTTPS client for the cloud ingestion API. The custom agent token is sent in
/// X-API-Token, while Google/API Gateway authentication can use Authorization.
/// Batch files are converted from gzip NDJSON to the JSON envelope expected by
/// the tally-ingestion-api /sync endpoint. TLS validation is always enabled.
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

        var apiToken = ConfigStore.GetApiToken(config);
        if (!string.IsNullOrWhiteSpace(apiToken))
            _http.DefaultRequestHeaders.Add("X-API-Token", apiToken);

        _http.DefaultRequestHeaders.Add("X-Agent-Id", config.Cloud.AgentId);
        _http.DefaultRequestHeaders.Add("X-Environment", config.Cloud.Environment);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"TallyBigQueryAgent/{AgentInfo.Version}");
    }

    public async Task<PingResponse> PingAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "health"), ct);
        EnsureAuth(resp);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        string? timestamp = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("timestamp", out var value))
                timestamp = value.GetString();
        }
        catch (JsonException)
        {
            // A successful HTTP response is sufficient for the connection test.
        }

        return new PingResponse { Ok = true, ServerTime = timestamp };
    }

    /// <summary>Upload one queued batch (payload is gzip NDJSON on disk).
    /// The envelope carries the full integrity/ordering metadata so the server
    /// can verify checksums, dedupe/replace by window, order by sequence, and
    /// feed reconciliation — this metadata was dropped in the first /sync
    /// migration and is restored here (contract v2.1).</summary>
    public async Task<BatchResponse> UploadBatchAsync(QueuedBatch batch, CancellationToken ct = default)
    {
        var records = await ReadBatchRecordsAsync(batch.PayloadPath, ct);
        var payload = new
        {
            agent_id = _config.Cloud.AgentId,
            company_id = _config.Cloud.CompanyId,
            batch_id = batch.BatchId,
            dataset_name = batch.Dataset,
            tally_company = batch.Company,
            sequence_no = batch.SequenceNo,
            sync_id = batch.SyncId,
            record_count = batch.RecordCount,
            checksum_sha256 = batch.ChecksumSha256,      // transport checksum (gzip file)
            content_checksum = batch.ContentChecksum,    // identity checksum (audit-free rows)
            schema_version = batch.SchemaVersion,
            agent_version = AgentInfo.Version,
            window_from = batch.WindowFrom,
            window_to = batch.WindowTo,
            extract_start = batch.ExtractStartUtc,
            extracted_at = batch.ExtractEndUtc,
            retry_count = batch.RetryCount,
            records,
        };

        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "sync")
        {
            Content = JsonContent.Create(payload),
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

    /// <summary>Heartbeat on the SAME base + auth scheme as health/sync (one
    /// contract, not two). Failures are wrapped in CloudApiException so the
    /// caller can categorize instead of receiving raw HttpRequestException.</summary>
    public async Task<HeartbeatResponse> SendHeartbeatAsync(HeartbeatRequest hb, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "heartbeat")
        { Content = JsonContent.Create(hb) }, ct);
        EnsureAuth(resp);
        EnsureSuccess(resp, "heartbeat");
        try
        {
            return await resp.Content.ReadFromJsonAsync<HeartbeatResponse>(ct)
                   ?? new HeartbeatResponse { Ok = true };
        }
        catch (JsonException)
        {
            return new HeartbeatResponse { Ok = true }; // 2xx with non-JSON body: delivered
        }
    }

    public async Task ReportErrorAsync(ErrorReportRequest report, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "errors")
        { Content = JsonContent.Create(report) }, ct);
        EnsureAuth(resp);
        EnsureSuccess(resp, "error report");
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"updates/check?current={AgentInfo.Version}&channel={_config.Cloud.Environment.ToLowerInvariant()}"), ct);
        if (resp.StatusCode == HttpStatusCode.NoContent || resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        EnsureAuth(resp);
        EnsureSuccess(resp, "update check");
        var info = await resp.Content.ReadFromJsonAsync<UpdateInfo>(ct);
        return string.IsNullOrEmpty(info?.Version) ? null : info;
    }

    private static async Task<List<JsonElement>> ReadBatchRecordsAsync(
        string payloadPath, CancellationToken ct)
    {
        var records = new List<JsonElement>();
        await using var file = File.OpenRead(payloadPath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            records.Add(document.RootElement.Clone());
        }

        return records;
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

    /// <summary>Non-2xx → categorized CloudApiException (never a raw
    /// HttpRequestException from EnsureSuccessStatusCode, which callers can't
    /// classify or schedule retries from).</summary>
    private static void EnsureSuccess(HttpResponseMessage resp, string operation)
    {
        if (!resp.IsSuccessStatusCode)
            throw new CloudApiException(ErrorCategory.CloudApiUnavailable,
                $"Ingestion API {operation} returned HTTP {(int)resp.StatusCode}.",
                retryable: true, RetryAfterOf(resp));
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
