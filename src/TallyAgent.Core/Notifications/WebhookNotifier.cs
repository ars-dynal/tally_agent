using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Security;

namespace TallyAgent.Core.Notifications;

/// <summary>Pushes alert text to Google Chat, Slack and/or a generic JSON webhook.
/// These are the direct fallback channels — the primary path is the cloud API's
/// /v1/errors endpoint which fans out server-side.</summary>
public sealed class WebhookNotifier(AgentConfig config, ILogger<WebhookNotifier> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task SendAsync(string title, string body, CancellationToken ct = default)
    {
        var text = $"*{title}*\n{body}";

        var gchat = ConfigStore.GetGoogleChatWebhook(config);
        if (!string.IsNullOrWhiteSpace(gchat))
            await PostSafe(gchat, new { text }, "GoogleChat", ct);

        var slack = ConfigStore.GetSlackWebhook(config);
        if (!string.IsNullOrWhiteSpace(slack))
            await PostSafe(slack, new { text }, "Slack", ct);

        var generic = ConfigStore.GetErrorWebhook(config);
        if (!string.IsNullOrWhiteSpace(generic))
            await PostSafe(generic, new
            {
                title,
                message = body,
                agent_id = config.Cloud.AgentId,
                environment = config.Cloud.Environment,
                timestamp_utc = DateTime.UtcNow.ToString("O"),
            }, "Webhook", ct);
    }

    private async Task PostSafe(string url, object payload, string channel, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.PostAsJsonAsync(url, payload, ct);
            if (!resp.IsSuccessStatusCode)
                log.LogWarning("{Channel} notification returned HTTP {Code}", channel, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            // Notifications must never take the agent down.
            log.LogWarning("{Channel} notification failed: {Msg}", channel, SecretMasker.Scrub(ex.Message));
        }
    }
}
