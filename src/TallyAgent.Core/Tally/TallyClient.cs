using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Notifications;

namespace TallyAgent.Core.Tally;

public sealed record TallyProbeResult(bool Ok, IReadOnlyList<string> Companies, string? Error,
    ErrorCategory? Category = null);

/// <summary>
/// HTTP transport to the local TallyPrime XML server.
///
/// SERVER PROTECTION (fix/tally-agent-server-protection):
/// Tally's XML server shares the application thread and must be treated as a
/// constrained dependency. Every HTTP request from EVERY process (service,
/// Manager test button, CLI test-tally / capture-xml) funnels through
/// <see cref="PostOnceAsync"/>, which holds:
///   1. an in-process gate (SemaphoreSlim, default concurrency 1, max 2), and
///   2. a cross-process gate (exclusive lock file under ProgramData\locks —
///      released by the OS if the holder crashes),
/// for the FULL request/response lifecycle. Gate waits are bounded and
/// cancellation-aware; a gate timeout surfaces as the transient
/// <see cref="ErrorCategory.TallyBusy"/> — never a second concurrent request.
///
/// RUNAWAY-WORK PROTECTION:
///  • timeout ladder (10/30/60s ± jitter) is bounded per request AND debits a
///    per-run retry budget (<see cref="ResetRunBudget"/>) — a stalling Tally
///    cannot be hammered indefinitely across datasets/windows;
///  • a client-side timeout aborts only our socket while Tally keeps working,
///    so auto-reconnect now TCP-probes first and only re-sends the full
///    request once the port answers again — no full-payload polling;
///  • cancellation is never retried; all waits honour the caller token.
/// </summary>
public sealed class TallyClient : IDisposable
{
    private static readonly TimeSpan[] TimeoutRetryBackoff =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private static readonly TimeSpan GatePollInterval = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _http;
    private readonly ILogger<TallyClient> _log;
    private readonly TallySettings _settings;
    private readonly SemaphoreSlim _localGate;
    private readonly string _gateLockPath;
    private int _runRetryBudget = int.MaxValue;

    /// <summary>Injectable delay for deterministic offline tests (no real sleeps).</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    public TallyClient(TallySettings settings, ILogger<TallyClient> log, HttpClient? http = null,
        string? gateLockDirOverride = null)
    {
        _settings = settings;
        _log = log;
        _http = http ?? new HttpClient();
        // Timeouts are enforced per request (PostOnceAsync) so heavy voucher
        // windows can use a longer budget than light master/report calls.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _localGate = new SemaphoreSlim(settings.EffectiveMaxConcurrentTallyRequests,
                                       settings.EffectiveMaxConcurrentTallyRequests);
        var lockDir = gateLockDirOverride ?? Path.Combine(AgentInfo.DataDir, "locks");
        Directory.CreateDirectory(lockDir);
        _gateLockPath = Path.Combine(lockDir, "tally-gate.lock");
    }

    public string Company => _settings.Company;

    /// <summary>Request budget for windowed voucher extraction — the heaviest
    /// Tally call. Never below the general request timeout.</summary>
    public TimeSpan VoucherRequestTimeout =>
        TimeSpan.FromSeconds(Math.Max(_settings.RequestTimeoutSeconds,
            _settings.VoucherTimeoutSeconds));

    /// <summary>Arm the per-run retry budget (call at the start of each sync
    /// cycle). Every timeout-retry and reconnect-resend debits it; when it is
    /// exhausted the current request fails with a NON-retryable TallyTimeout so
    /// the run ends cleanly and resumes from its checkpoint next cycle.</summary>
    public void ResetRunBudget(int totalRetries) =>
        _runRetryBudget = Math.Max(0, totalRetries);

    /// <summary>Fast TCP probe + company list. Distinguishes "not running" from
    /// "port blocked" from "company not open" from "company mismatch".</summary>
    public async Task<TallyProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        // 1. raw TCP reachability (cheap; does not consume the request gate)
        if (!await TryTcpProbeAsync(ct))
            return new TallyProbeResult(false, [],
                $"Nothing is listening on {_settings.Host}:{_settings.Port}. " +
                "Ensure TallyPrime is running and 'TallyPrime acts as' is set to 'Both' or 'Server' " +
                "(F1 > Settings > Connectivity > Client/Server configuration).",
                ErrorCategory.TallyNotRunning);

        // 2. XML company list (gated like every other request)
        try
        {
            var doc = await PostOnceAsync(TallyEnvelopes.CompanyList(), null, ct);
            var companies = doc.Descendants("NAME")
                .Select(e => e.Value.Trim())
                .Where(v => v.Length > 0)
                .Distinct()
                .ToList();

            if (!string.IsNullOrWhiteSpace(_settings.Company) &&
                !companies.Contains(_settings.Company, StringComparer.OrdinalIgnoreCase))
            {
                // Distinct operator-actionable conditions (§E4): no company open
                // at all vs a DIFFERENT company open than the configured one.
                return companies.Count == 0
                    ? new TallyProbeResult(false, companies,
                        $"No company is open in Tally (expected '{_settings.Company}').",
                        ErrorCategory.TallyCompanyNotOpen)
                    : new TallyProbeResult(false, companies,
                        $"Configured company '{_settings.Company}' is not among the open companies. " +
                        $"Open: {string.Join(", ", companies)}",
                        ErrorCategory.TallyCompanyMismatch);
            }

            return new TallyProbeResult(true, companies, null);
        }
        catch (TallyException tex)
        {
            return new TallyProbeResult(false, [], tex.Message, tex.Category);
        }
    }

    /// <summary>POST an envelope and return the RAW sanitized response text —
    /// used by the capture-xml diagnostics verb to persist real Tally responses
    /// as validation fixtures (ARCHITECTURE §8.4 extraction-validation gate).</summary>
    public async Task<string> PostRawAsync(string envelope, CancellationToken ct = default)
    {
        var doc = await PostAsync(envelope, ct);
        return doc.ToString(SaveOptions.None);
    }

    /// <summary>Company-wide AlterID watermarks for the configured company —
    /// (masters, vouchers), or null when the Tally build doesn't expose them
    /// (callers must treat null as "always changed").</summary>
    public async Task<(long Masters, long Vouchers)?> GetCompanyAlterIdsAsync(CancellationToken ct = default)
    {
        var doc = await PostAsync(TallyEnvelopes.CompanyAlterIds(_settings.Company), ct);
        foreach (var el in doc.Descendants("COMPANY"))
        {
            var name = TallyXml.Text(el, "NAME");
            if (!string.IsNullOrWhiteSpace(_settings.Company) &&
                !name.Equals(_settings.Company, StringComparison.OrdinalIgnoreCase))
                continue;
            var m = TallyXml.Int(el, "ALTMSTID");
            var v = TallyXml.Int(el, "ALTVCHID");
            if (m > 0 || v > 0) return (m, v);
        }
        return null;
    }

    public Task<XDocument> PostAsync(string envelope, CancellationToken ct = default) =>
        PostAsync(envelope, null, null, ct);

    /// <summary>
    /// POST with retry/reconnect resilience. Voucher window extraction passes
    /// maxTimeoutRetries: 0 for multi-day windows — retrying an identical heavy
    /// request at the same size is deterministic waste; splitting converges faster.
    /// </summary>
    public async Task<XDocument> PostAsync(string envelope, TimeSpan? requestTimeout,
        int? maxTimeoutRetries, CancellationToken ct = default)
    {
        var reconnectDeadline = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.ReconnectMaxMinutes));
        var reconnectDelay = TimeSpan.FromSeconds(Math.Max(5, _settings.ReconnectRetrySeconds));
        var timeoutRetries = Math.Clamp(maxTimeoutRetries ?? TimeoutRetryBackoff.Length,
            0, TimeoutRetryBackoff.Length);
        var timeoutAttempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested(); // cancellation is never retried (§F6)
            try
            {
                return await PostOnceAsync(envelope, requestTimeout, ct);
            }
            catch (TallyException tex) when (tex.Category == ErrorCategory.TallyTimeout)
            {
                if (timeoutAttempt >= timeoutRetries)
                    throw;
                DebitRunBudget(tex);

                // ±20% jitter prevents synchronized retry storms across datasets.
                var baseDelay = TimeoutRetryBackoff[timeoutAttempt++];
                var delay = baseDelay * (0.8 + Random.Shared.NextDouble() * 0.4);
                _log.LogWarning("Tally request timed out (attempt {N}): {Msg} — retrying in {Delay:F0}s",
                    timeoutAttempt, tex.Message, delay.TotalSeconds);
                await DelayAsync(delay, ct);
            }
            catch (TallyException tex) when (tex.Category is ErrorCategory.TallyNotRunning
                or ErrorCategory.TallyPortUnavailable)
            {
                if (DateTime.UtcNow >= reconnectDeadline)
                {
                    _log.LogError("Tally did not recover within {Minutes} minute(s); preserving checkpoint for resume",
                        Math.Max(1, _settings.ReconnectMaxMinutes));
                    throw;
                }
                DebitRunBudget(tex);

                timeoutAttempt = 0;
                var remaining = Math.Max(0, (int)Math.Ceiling((reconnectDeadline - DateTime.UtcNow).TotalMinutes));
                _log.LogWarning(
                    "Tally connection dropped: {Msg} — probing for recovery every {Delay}s (up to {Remaining} min remaining)",
                    tex.Message, reconnectDelay.TotalSeconds, remaining);

                // Probe-first reconnect: wait, then check the TCP port cheaply and
                // ONLY re-send the full request once Tally answers again. The old
                // behaviour re-posted the entire payload every interval, piling
                // work onto a Tally instance that was likely still busy.
                do
                {
                    await DelayAsync(reconnectDelay, ct);
                    if (await TryTcpProbeAsync(ct)) break;
                } while (DateTime.UtcNow < reconnectDeadline);
            }
        }
    }

    // ── single request: gate → send → bounded read → parse ──────────────

    private async Task<XDocument> PostOnceAsync(string envelope, TimeSpan? requestTimeout,
        CancellationToken ct)
    {
        var budget = requestTimeout
            ?? TimeSpan.FromSeconds(Math.Max(1, _settings.RequestTimeoutSeconds));

        // In-process + cross-process gates held for the WHOLE request/response
        // lifecycle (§D9). Bounded, cancellation-aware acquisition.
        var gateWait = TimeSpan.FromSeconds(Math.Clamp(_settings.GateWaitSeconds, 5, 600));
        if (!await _localGate.WaitAsync(gateWait, ct))
            throw new TallyException(ErrorCategory.TallyBusy,
                $"Another Tally request is still in flight after waiting {gateWait.TotalSeconds:F0}s " +
                "(in-process gate). The request was NOT sent.");
        FileStream? crossLock = null;
        try
        {
            crossLock = await AcquireCrossProcessGateAsync(gateWait, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(budget);

            HttpResponseMessage? resp = null;
            byte[] body;
            try
            {
                using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(envelope));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/xml");
                resp = await _http.PostAsync(_settings.BaseUri, content, timeoutCts.Token);
                body = await ReadBoundedBodyAsync(resp, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                resp?.Dispose(); // connection not pinned while the retry loop runs
                throw new TallyException(ErrorCategory.TallyTimeout,
                    $"Tally request timed out after {(int)budget.TotalSeconds}s " +
                    $"({_settings.Host}:{_settings.Port}).");
            }
            catch (TallyException)
            {
                resp?.Dispose(); // e.g. TallyResponseTooLarge from the bounded read
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new TallyException(ErrorCategory.TallyNotRunning,
                    $"Cannot reach Tally at {_settings.Host}:{_settings.Port}: {ex.Message}", ex);
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    throw new TallyException(ErrorCategory.TallyPortUnavailable,
                        $"Tally returned HTTP {(int)resp.StatusCode} on {_settings.Host}:{_settings.Port}.");
            }

            try
            {
                return TallyXml.Parse(body);
            }
            catch (Exception ex)
            {
                var preview = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 300));
                throw new TallyException(ErrorCategory.TallyInvalidXml,
                    $"Tally response is not valid XML: {ex.Message}. Response starts with: {preview}", ex);
            }
        }
        finally
        {
            crossLock?.Dispose();   // OS-backed: released even if this process dies
            _localGate.Release();
        }
    }

    /// <summary>Exclusive lock file shared by service, Manager and CLI so a
    /// diagnostics probe can never hit Tally while an extraction is in flight.
    /// Crash-safe: the OS releases the handle when the holder exits.</summary>
    private async Task<FileStream> AcquireCrossProcessGateAsync(TimeSpan wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_gateLockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // UnauthorizedAccessException covers the Windows delete-pending
                // window while a DeleteOnClose holder is releasing the file.
                if (DateTime.UtcNow >= deadline)
                    throw new TallyException(ErrorCategory.TallyBusy,
                        $"Another process is talking to Tally (gate busy for {wait.TotalSeconds:F0}s). " +
                        "The request was NOT sent.");
                await DelayAsync(GatePollInterval, ct);
            }
        }
    }

    /// <summary>Read the response body with a hard byte cap (§G10) so a runaway
    /// export cannot exhaust agent memory. Oversized responses fail with a
    /// NON-retryable error naming the offending size.</summary>
    private async Task<byte[]> ReadBoundedBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var capBytes = (long)Math.Clamp(_settings.MaxResponseMb, 16, 1024) * 1024 * 1024;
        if (resp.Content.Headers.ContentLength is { } len && len > capBytes)
            throw new TallyException(ErrorCategory.TallyResponseTooLarge,
                $"Tally response ({len / (1024 * 1024)} MB) exceeds the {_settings.MaxResponseMb} MB limit. " +
                "Reduce the extraction window or raise tally.maxResponseMb.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > capBytes)
                throw new TallyException(ErrorCategory.TallyResponseTooLarge,
                    $"Tally response exceeded the {_settings.MaxResponseMb} MB limit mid-stream. " +
                    "Reduce the extraction window or raise tally.maxResponseMb.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task<bool> TryTcpProbeAsync(CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            // Real 5s timeout via WaitAsync — deliberately NOT the injectable
            // DelayAsync: an instant test delay must not zero the probe window.
            await tcp.ConnectAsync(_settings.Host, _settings.Port, ct).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), ct);
            return tcp.Connected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private void DebitRunBudget(TallyException cause)
    {
        if (_runRetryBudget == int.MaxValue) return; // budget not armed (CLI/Manager)
        if (_runRetryBudget <= 0)
            throw new TallyException(ErrorCategory.TallyTimeout,
                "Per-run Tally retry budget exhausted — ending this sync cycle so Tally can " +
                $"recover; the run resumes from its checkpoint next cycle. Last error: {cause.Message}");
        _runRetryBudget--;
    }

    public void Dispose()
    {
        _localGate.Dispose();
        _http.Dispose();
    }
}
