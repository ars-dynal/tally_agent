using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;

namespace TallyAgent.Core.Notifications;

/// <summary>
/// Tells somebody when a run goes wrong.
///
/// The agent stalled on 4-Sep and failed on 5-Sep. Both times the way anyone
/// found out was Imran opening a console. This does not need to be elegant; it
/// needs to fire.
///
/// Four conditions:
///   • a run FAILED
///   • a run finished with datasets MISSING
///   • a run is STALLED — running, but no progress for a while
///   • the upload queue has STOPPED DRAINING
///
/// Sends to a webhook, an SMTP mailbox, or both, whichever is configured. If
/// NEITHER is configured it says so loudly in the log at startup, because an
/// alerter nobody has wired up is worse than none — it looks like cover.
/// </summary>
public sealed class RunAlerter(AgentConfig config, WebhookNotifier webhook, ILogger<RunAlerter> log)
{
    private DateTime _lastAlertUtc = DateTime.MinValue;
    private string _lastAlertKey = "";

    private TimeSpan Cooldown =>
        TimeSpan.FromMinutes(Math.Max(1, config.Notifications.CriticalAlertCooldownMinutes));

    public bool AnyChannelConfigured =>
        !string.IsNullOrWhiteSpace(ConfigStore.GetErrorWebhook(config)) ||
        !string.IsNullOrWhiteSpace(ConfigStore.GetGoogleChatWebhook(config)) ||
        !string.IsNullOrWhiteSpace(ConfigStore.GetSlackWebhook(config)) ||
        SmtpConfigured;

    private bool SmtpConfigured =>
        config.Notifications.EnableEmailAlerts &&
        !string.IsNullOrWhiteSpace(config.Notifications.SmtpHost) &&
        !string.IsNullOrWhiteSpace(config.Notifications.AdminEmail);

    /// <summary>Say once, at startup, whether anybody will actually be told.</summary>
    public void LogChannelStatus()
    {
        if (AnyChannelConfigured)
            log.LogInformation("Failure alerts are enabled ({Channels}).", string.Join(" + ",
                new[]
                {
                    !string.IsNullOrWhiteSpace(ConfigStore.GetErrorWebhook(config)) ? "webhook" : null,
                    !string.IsNullOrWhiteSpace(ConfigStore.GetGoogleChatWebhook(config)) ? "Google Chat" : null,
                    !string.IsNullOrWhiteSpace(ConfigStore.GetSlackWebhook(config)) ? "Slack" : null,
                    SmtpConfigured ? $"email to {config.Notifications.AdminEmail}" : null,
                }.Where(x => x is not null)));
        else
            log.LogWarning(
                "NO failure alerting is configured — a failed or stalled sync will not notify anyone. " +
                "Set notifications.errorWebhookUrl, or notifications.smtpHost with adminEmail.");
    }

    public Task RunFailedAsync(RunRecord run, CancellationToken ct = default)
    {
        var failures = run.Failures();
        var body =
            $"The Tally sync FAILED.\n\n" +
            $"When    : {Local(run.FinishedUtc ?? run.StartedUtc)}\n" +
            $"Mode    : {run.Mode}\n" +
            $"Window  : {(run.WindowFrom is null ? "masters and reports only" : $"{run.WindowFrom} to {run.WindowTo}")}\n" +
            $"Loaded  : {run.DatasetsSucceeded} of {run.DatasetsAttempted} datasets\n\n" +
            (failures.Count > 0
                ? "Did not load:\n" + string.Join("\n", failures.Select(f => $"  • {f.Dataset} — {f.Reason}"))
                : run.ErrorMessage ?? "") +
            "\n\nWhat to do: open the agent console on the Tally server and check the Problems tab.";
        return SendAsync($"Tally sync FAILED — {run.DatasetsAttempted - run.DatasetsSucceeded} datasets not loaded",
            body, key: "run-failed", ct);
    }

    public Task RunIncompleteAsync(RunRecord run, CancellationToken ct = default)
    {
        var failures = run.Failures();
        var body =
            $"The Tally sync finished but some datasets did not load.\n\n" +
            $"When    : {Local(run.FinishedUtc ?? run.StartedUtc)}\n" +
            $"Loaded  : {run.DatasetsSucceeded} of {run.DatasetsAttempted} datasets\n" +
            $"Records : {run.RecordsQueued:N0}\n\n" +
            "Did not load:\n" +
            string.Join("\n", failures.Select(f => $"  • {f.Dataset} — {f.Reason}"));
        return SendAsync($"Tally sync incomplete — {failures.Count} datasets missing", body,
            key: "run-partial", ct);
    }

    public Task RunStalledAsync(string operation, DateTime startedUtc, int idleMinutes,
        CancellationToken ct = default) =>
        SendAsync("Tally sync appears STALLED",
            $"A sync has been running since {Local(startedUtc.ToString("O"))} with no progress " +
            $"for {idleMinutes} minutes.\n\n" +
            $"Last thing it was doing: {operation}\n\n" +
            "What to do: check whether TallyPrime is still responding on the server. " +
            "A stalled run usually means Tally is busy or has stopped answering.",
            key: "run-stalled", ct);

    public Task QueueNotDrainingAsync(long pending, long failed, string? oldestUtc,
        CancellationToken ct = default) =>
        SendAsync("Tally agent: uploads are not draining",
            $"{pending:N0} batch(es) are waiting to upload and {failed:N0} are stuck.\n" +
            $"Oldest waiting since {Local(oldestUtc)}.\n\n" +
            "Extracted data is safe on disk and will upload by itself once the problem clears.\n\n" +
            "What to do: check internet access from the server, and that the ingestion API is up. " +
            "If credentials changed, update the API token in the agent's settings.",
            key: "queue-stuck", ct);

    private static string Local(string? utc) =>
        DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("dd MMM yyyy HH:mm") : "unknown";

    /// <summary>One alert per condition per cooldown — a stalled run must not
    /// send a message every polling interval for an hour.</summary>
    private async Task SendAsync(string title, string body, string key, CancellationToken ct)
    {
        if (!AnyChannelConfigured)
        {
            log.LogError("ALERT (nowhere to send it): {Title}\n{Body}", title, body);
            return;
        }
        if (_lastAlertKey == key && DateTime.UtcNow - _lastAlertUtc < Cooldown)
        {
            log.LogDebug("Alert '{Key}' suppressed (cooldown).", key);
            return;
        }
        _lastAlertKey = key;
        _lastAlertUtc = DateTime.UtcNow;

        try { await webhook.SendAsync(title, body, ct); }
        catch (Exception ex) { log.LogWarning("Webhook alert failed ({Msg})", ex.Message); }

        if (SmtpConfigured)
        {
            try { await SendMailAsync(title, body, ct); }
            catch (Exception ex) { log.LogWarning("Email alert failed ({Msg})", ex.Message); }
        }
    }

    private async Task SendMailAsync(string subject, string body, CancellationToken ct)
    {
        var n = config.Notifications;
        using var client = new SmtpClient(n.SmtpHost, n.SmtpPort) { EnableSsl = n.SmtpUseTls };
        var user = n.SmtpUser;
        var pass = ConfigStore.GetSmtpPassword(config);
        if (!string.IsNullOrWhiteSpace(user))
            client.Credentials = new NetworkCredential(user, pass);

        var from = string.IsNullOrWhiteSpace(n.SmtpFrom) ? n.AdminEmail : n.SmtpFrom;
        using var mail = new MailMessage(from, n.AdminEmail, subject, body);
        await client.SendMailAsync(mail, ct);
        log.LogInformation("Alert emailed to {To}", n.AdminEmail);
    }
}
