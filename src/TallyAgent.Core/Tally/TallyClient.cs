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
/// HTTP transport to the local TallyPrime XML server with categorized failures
/// and bounded retries (10s / 30s / 60s backoff).
/// </summary>
public sealed class TallyClient
{
    private static readonly TimeSpan[] RetryBackoff =
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

    /// <summary>POST an envelope with bounded retries; parses + sanitizes the response.</summary>
    public async Task<XDocument> PostAsync(string envelope, CancellationToken ct = default)
    {
        TallyException? last = null;
        for (var attempt = 0; attempt <= RetryBackoff.Length; attempt++)
        {
            try
            {
                return await PostOnceAsync(envelope, ct);
            }
            catch (TallyException tex) when (tex.Category is ErrorCategory.TallyTimeout
                or ErrorCategory.TallyNotRunning or ErrorCategory.TallyPortUnavailable)
            {
                last = tex;
                if (attempt < RetryBackoff.Length)
                {
                    _log.LogWarning("Tally request failed (attempt {N}): {Msg} — retrying in {Delay}s",
                        attempt + 1, tex.Message, RetryBackoff[attempt].TotalSeconds);
                    await Task.Delay(RetryBackoff[attempt], ct);
                }
            }
        }
        throw last!;
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
