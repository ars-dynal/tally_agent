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
/// HTTP transport to the local TallyPrime XML server with categorized failures,
/// bounded timeout retries and bounded auto-reconnect for temporary server drops.
/// </summary>
public sealed class TallyClient
{
    private static readonly TimeSpan[] TimeoutRetryBackoff =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private readonly HttpClient _http;
    private readonly ILogger<TallyClient> _log;
    private readonly TallySettings _settings;

    public TallyClient(TallySettings settings, ILogger<TallyClient> log, HttpClient? http = null)
    {
        _settings = settings;
        _log = log;
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
    }

    public string Company => _settings.Company;

    /// <summary>Fast TCP probe + company list. Distinguishes "not running" from
    /// "port blocked" from "company not open".</summary>
    public async Task<TallyProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        // 1. raw TCP reachability
        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(_settings.Host, _settings.Port, ct).AsTask();
            var done = await Task.WhenAny(connectTask, Task.Delay(5000, ct));
            if (done != connectTask || !tcp.Connected)
                return new TallyProbeResult(false, [],
                    $"Nothing is listening on {_settings.Host}:{_settings.Port}. " +
                    "Ensure TallyPrime is running and 'TallyPrime acts as' is set to 'Both' or 'Server' " +
                    "(F1 > Settings > Connectivity > Client/Server configuration).",
                    ErrorCategory.TallyNotRunning);
            await connectTask; // surface exceptions
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new TallyProbeResult(false, [],
                $"TCP connection to {_settings.Host}:{_settings.Port} failed: {ex.Message}",
                ErrorCategory.TallyPortUnavailable);
        }

        // 2. XML company list
        try
        {
            var doc = await PostOnceAsync(TallyEnvelopes.CompanyList(), ct);
            var companies = doc.Descendants("NAME")
                .Select(e => e.Value.Trim())
                .Where(v => v.Length > 0)
                .Distinct()
                .ToList();

            if (!string.IsNullOrWhiteSpace(_settings.Company) &&
                !companies.Contains(_settings.Company, StringComparer.OrdinalIgnoreCase))
                return new TallyProbeResult(false, companies,
                    $"Company '{_settings.Company}' is not open in Tally. Open companies: " +
                    (companies.Count > 0 ? string.Join(", ", companies) : "(none)"),
                    ErrorCategory.TallyCompanyNotOpen);

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
        return doc.ToString(System.Xml.Linq.SaveOptions.None);
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

    /// <summary>
    /// POST an envelope with two resilience modes:
    ///  • slow/heavy request timeout: retry 10s/30s/60s, then return TallyTimeout
    ///    so the SyncEngine can split the voucher window;
    ///  • Tally temporarily unreachable: keep probing the same request at a
    ///    bounded interval for up to reconnectMaxMinutes, then continue exactly
    ///    where the sync stopped when Tally comes back.
    /// </summary>
    public async Task<XDocument> PostAsync(string envelope, CancellationToken ct = default)
    {
        var reconnectDeadline = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.ReconnectMaxMinutes));
        var reconnectDelay = TimeSpan.FromSeconds(Math.Max(5, _settings.ReconnectRetrySeconds));
        var timeoutAttempt = 0;

        while (true)
        {
            try
            {
                return await PostOnceAsync(envelope, ct);
            }
            catch (TallyException tex) when (tex.Category == ErrorCategory.TallyTimeout)
            {
                if (timeoutAttempt >= TimeoutRetryBackoff.Length)
                    throw;

                var delay = TimeoutRetryBackoff[timeoutAttempt++];
                _log.LogWarning("Tally request timed out (attempt {N}): {Msg} — retrying in {Delay}s",
                    timeoutAttempt, tex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
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

                timeoutAttempt = 0;
                var remaining = Math.Max(0, (int)Math.Ceiling((reconnectDeadline - DateTime.UtcNow).TotalMinutes));
                _log.LogWarning(
                    "Tally connection dropped: {Msg} — auto-reconnect in {Delay}s (up to {Remaining} min remaining)",
                    tex.Message, reconnectDelay.TotalSeconds, remaining);
                await Task.Delay(reconnectDelay, ct);
            }
        }
    }

    private async Task<XDocument> PostOnceAsync(string envelope, CancellationToken ct)
    {
        HttpResponseMessage resp;
        byte[] body;
        try
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(envelope));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/xml");
            resp = await _http.PostAsync(_settings.BaseUri, content, ct);
            body = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TallyException(ErrorCategory.TallyTimeout,
                $"Tally request timed out after {_settings.RequestTimeoutSeconds}s " +
                $"({_settings.Host}:{_settings.Port}).");
        }
        catch (HttpRequestException ex)
        {
            throw new TallyException(ErrorCategory.TallyNotRunning,
                $"Cannot reach Tally at {_settings.Host}:{_settings.Port}: {ex.Message}", ex);
        }

        if (!resp.IsSuccessStatusCode)
            throw new TallyException(ErrorCategory.TallyPortUnavailable,
                $"Tally returned HTTP {(int)resp.StatusCode} on {_settings.Host}:{_settings.Port}.");

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
}