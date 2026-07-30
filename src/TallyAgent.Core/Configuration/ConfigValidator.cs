using System.Text.RegularExpressions;

namespace TallyAgent.Core.Configuration;

public sealed class ConfigValidationException(string message) : Exception(message);

/// <summary>Fail-fast validation for the configuration model.</summary>
public static partial class ConfigValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,64}$")]
    private static partial Regex IdRegex();

    private static readonly string[] Environments = ["Development", "Testing", "Production"];

    public static void Validate(AgentConfig cfg)
    {
        var errors = new List<string>();

        // Tally
        if (string.IsNullOrWhiteSpace(cfg.Tally.Host)) errors.Add("Tally host is required.");
        if (cfg.Tally.Port is < 1 or > 65535) errors.Add("Tally port must be 1-65535.");
        if (cfg.Tally.SyncFrequencyMinutes is < 5 or > 1440)
            errors.Add("Sync frequency must be between 5 and 1440 minutes.");
        if (cfg.Tally.IncrementalLookbackDays is < 0 or > 90)
            errors.Add("Incremental lookback must be 0-90 days.");
        if (cfg.Tally.FullSyncChunkDays is < 1 or > 366)
            errors.Add("Full-sync chunk size must be 1-366 days.");
        if (!string.IsNullOrWhiteSpace(cfg.Tally.ExtractionStartDate) &&
            !DateOnly.TryParse(cfg.Tally.ExtractionStartDate, out _))
            errors.Add($"Extraction start date '{cfg.Tally.ExtractionStartDate}' is not a valid yyyy-MM-dd date.");
        if (cfg.Tally.RequestTimeoutSeconds is < 10 or > 900)
            errors.Add("Tally request timeout must be 10-900 seconds.");

        // Cloud
        if (string.IsNullOrWhiteSpace(cfg.Cloud.IngestionApiUrl))
            errors.Add("Cloud ingestion API URL is required.");
        else if (!Uri.TryCreate(cfg.Cloud.IngestionApiUrl, UriKind.Absolute, out var uri))
            errors.Add("Cloud ingestion API URL is not a valid absolute URL.");
        else if (uri.Scheme != Uri.UriSchemeHttps &&
                 !cfg.Cloud.Environment.Equals("Development", StringComparison.OrdinalIgnoreCase))
            errors.Add("Cloud ingestion API URL must use HTTPS outside the Development environment.");

        if (!IdRegex().IsMatch(cfg.Cloud.AgentId ?? ""))
            errors.Add("Agent ID must be 1-64 chars of letters, digits, '.', '_' or '-'.");
        if (!IdRegex().IsMatch(cfg.Cloud.CompanyId ?? ""))
            errors.Add("Company ID must be 1-64 chars of letters, digits, '.', '_' or '-'.");
        if (!Environments.Contains(cfg.Cloud.Environment, StringComparer.OrdinalIgnoreCase))
            errors.Add("Environment must be Development, Testing or Production.");
        if (cfg.Cloud.UploadBatchMaxRecords is < 100 or > 50000)
            errors.Add("Upload batch max records must be 100-50000.");
        if (cfg.Cloud.HeartbeatMinutes is < 1 or > 60)
            errors.Add("Heartbeat interval must be 1-60 minutes.");

        // Notifications
        if (cfg.Notifications.EnableEmailAlerts &&
            !string.IsNullOrWhiteSpace(cfg.Notifications.AdminEmail) &&
            !cfg.Notifications.AdminEmail.Contains('@'))
            errors.Add("Admin email address is not valid.");

        if (errors.Count > 0)
            throw new ConfigValidationException("Configuration invalid:\n - " + string.Join("\n - ", errors));
    }
}
