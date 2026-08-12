using System.Text.Json.Serialization;

namespace TallyAgent.Core.Configuration;

/// <summary>Root configuration model persisted at C:\ProgramData\TallyBigQueryAgent\config.json.
/// Secret-valued fields are stored DPAPI-encrypted ("dpapi:...") — use ConfigStore to
/// read decrypted values; never serialize decrypted secrets back to disk.</summary>
public sealed class AgentConfig
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = AgentInfo.SchemaVersion;
    [JsonPropertyName("tally")] public TallySettings Tally { get; set; } = new();
    [JsonPropertyName("cloud")] public CloudSettings Cloud { get; set; } = new();
    [JsonPropertyName("notifications")] public NotificationSettings Notifications { get; set; } = new();
    [JsonPropertyName("advanced")] public AdvancedSettings Advanced { get; set; } = new();
}

public sealed class TallySettings
{
    [JsonPropertyName("host")] public string Host { get; set; } = "127.0.0.1";
    [JsonPropertyName("port")] public int Port { get; set; } = 9000;
    [JsonPropertyName("company")] public string Company { get; set; } = "";
    /// <summary>ISO date (yyyy-MM-dd) from which historical vouchers are extracted.</summary>
    [JsonPropertyName("extractionStartDate")] public string ExtractionStartDate { get; set; } = "";
    [JsonPropertyName("syncFrequencyMinutes")] public int SyncFrequencyMinutes { get; set; } = 15;
    [JsonPropertyName("requestTimeoutSeconds")] public int RequestTimeoutSeconds { get; set; } = 120;
    /// <summary>Timeout for windowed voucher extraction requests, which are the
    /// heaviest calls the agent makes. Defaults higher than requestTimeoutSeconds
    /// so a large month gets a fair chance before the window is split.</summary>
    [JsonPropertyName("voucherTimeoutSeconds")] public int VoucherTimeoutSeconds { get; set; } = 300;
    /// <summary>How long an active sync waits for a temporarily unavailable Tally server before giving up.</summary>
    [JsonPropertyName("reconnectMaxMinutes")] public int ReconnectMaxMinutes { get; set; } = 30;
    /// <summary>Delay between Tally reachability probes while auto-reconnecting.</summary>
    [JsonPropertyName("reconnectRetrySeconds")] public int ReconnectRetrySeconds { get; set; } = 30;
    [JsonPropertyName("autoDiscoverCompanies")] public bool AutoDiscoverCompanies { get; set; } = true;
    [JsonPropertyName("enableMasters")] public bool EnableMasters { get; set; } = true;
    [JsonPropertyName("enableVouchers")] public bool EnableVouchers { get; set; } = true;
    [JsonPropertyName("enableInventory")] public bool EnableInventory { get; set; } = true;
    [JsonPropertyName("enableGst")] public bool EnableGst { get; set; } = true;
    [JsonPropertyName("enableCostCentres")] public bool EnableCostCentres { get; set; } = true;
    [JsonPropertyName("incrementalLookbackDays")] public int IncrementalLookbackDays { get; set; } = 7;
    [JsonPropertyName("fullSyncChunkDays")] public int FullSyncChunkDays { get; set; } = 31;

    public Uri BaseUri => new($"http://{Host}:{Port}/");
}

public sealed class CloudSettings
{
    [JsonPropertyName("ingestionApiUrl")] public string IngestionApiUrl { get; set; } = "";
    [JsonPropertyName("agentId")] public string AgentId { get; set; } = "";
    [JsonPropertyName("companyId")] public string CompanyId { get; set; } = "";
    /// <summary>DPAPI-protected at rest.</summary>
    [JsonPropertyName("apiToken")] public string ApiToken { get; set; } = "";
    [JsonPropertyName("environment")] public string Environment { get; set; } = "Production";
    [JsonPropertyName("uploadBatchMaxRecords")] public int UploadBatchMaxRecords { get; set; } = 5000;
    [JsonPropertyName("heartbeatMinutes")] public int HeartbeatMinutes { get; set; } = 5;
}

public sealed class NotificationSettings
{
    [JsonPropertyName("adminEmail")] public string AdminEmail { get; set; } = "";
    [JsonPropertyName("enableEmailAlerts")] public bool EnableEmailAlerts { get; set; } = true;
    /// <summary>DPAPI-protected at rest (webhook URLs embed tokens).</summary>
    [JsonPropertyName("errorWebhookUrl")] public string ErrorWebhookUrl { get; set; } = "";
    [JsonPropertyName("googleChatWebhookUrl")] public string GoogleChatWebhookUrl { get; set; } = "";
    [JsonPropertyName("slackWebhookUrl")] public string SlackWebhookUrl { get; set; } = "";
    [JsonPropertyName("criticalAlertCooldownMinutes")] public int CriticalAlertCooldownMinutes { get; set; } = 30;
    [JsonPropertyName("summaryIntervalMinutes")] public int SummaryIntervalMinutes { get; set; } = 60;
    [JsonPropertyName("enableDailyHealthSummary")] public bool EnableDailyHealthSummary { get; set; } = true;
    /// <summary>Server-local hour (0-23) for the once-daily remote health summary.</summary>
    [JsonPropertyName("dailyHealthHourLocal")] public int DailyHealthHourLocal { get; set; } = 8;
}

public sealed class AdvancedSettings
{
    [JsonPropertyName("logLevel")] public string LogLevel { get; set; } = "Information";
    [JsonPropertyName("queueDiskLimitMb")] public int QueueDiskLimitMb { get; set; } = 2048;
    [JsonPropertyName("minFreeDiskMb")] public int MinFreeDiskMb { get; set; } = 500;
    [JsonPropertyName("maxUploadRetryMinutes")] public int MaxUploadRetryMinutes { get; set; } = 30;
}