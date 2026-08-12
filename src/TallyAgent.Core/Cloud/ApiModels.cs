using System.Text.Json.Serialization;

namespace TallyAgent.Core.Cloud;

public sealed class PingResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("server_time")] public string? ServerTime { get; set; }
}

public sealed class BatchResponse
{
    /// <summary>"accepted" | "duplicate" | "rejected"</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("batch_id")] public string? BatchId { get; set; }
    [JsonPropertyName("errors")] public List<string>? Errors { get; set; }
}

public sealed class HeartbeatRequest
{
    [JsonPropertyName("agent_id")] public string AgentId { get; set; } = "";
    [JsonPropertyName("company_id")] public string CompanyId { get; set; } = "";
    [JsonPropertyName("machine_name")] public string MachineName { get; set; } = "";
    [JsonPropertyName("windows_version")] public string WindowsVersion { get; set; } = "";
    [JsonPropertyName("agent_version")] public string AgentVersion { get; set; } = "";
    [JsonPropertyName("environment")] public string Environment { get; set; } = "";
    [JsonPropertyName("service_status")] public string ServiceStatus { get; set; } = "";
    [JsonPropertyName("tally_connected")] public bool TallyConnected { get; set; }
    [JsonPropertyName("tally_company_open")] public bool TallyCompanyOpen { get; set; }
    [JsonPropertyName("tally_company")] public string TallyCompany { get; set; } = "";
    [JsonPropertyName("last_successful_sync_utc")] public string? LastSuccessfulSyncUtc { get; set; }
    [JsonPropertyName("last_attempted_sync_utc")] public string? LastAttemptedSyncUtc { get; set; }
    [JsonPropertyName("current_operation")] public string CurrentOperation { get; set; } = "";
    [JsonPropertyName("pending_batches")] public long PendingBatches { get; set; }
    [JsonPropertyName("failed_batches")] public long FailedBatches { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
    [JsonPropertyName("disk_free_mb")] public long DiskFreeMb { get; set; }
    [JsonPropertyName("memory_used_mb")] public long MemoryUsedMb { get; set; }
    [JsonPropertyName("internet_connected")] public bool InternetConnected { get; set; }
    [JsonPropertyName("timestamp_utc")] public string TimestampUtc { get; set; } = "";
}

public sealed class HeartbeatResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("commands")] public List<AgentCommand>? Commands { get; set; }
}

public sealed class AgentCommand
{
    /// <summary>"sync_now" | "update"</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public sealed class ErrorReportRequest
{
    [JsonPropertyName("agent_id")] public string AgentId { get; set; } = "";
    [JsonPropertyName("company_id")] public string CompanyId { get; set; } = "";
    [JsonPropertyName("machine_name")] public string MachineName { get; set; } = "";
    [JsonPropertyName("company_name")] public string CompanyName { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("stack_trace")] public string? StackTrace { get; set; }
    [JsonPropertyName("timestamp_utc")] public string TimestampUtc { get; set; } = "";
    [JsonPropertyName("operation")] public string? Operation { get; set; }
    [JsonPropertyName("dataset")] public string? Dataset { get; set; }
    [JsonPropertyName("batch_id")] public string? BatchId { get; set; }
    [JsonPropertyName("retry_count")] public int RetryCount { get; set; }
    [JsonPropertyName("agent_version")] public string AgentVersion { get; set; } = "";
    [JsonPropertyName("is_summary")] public bool IsSummary { get; set; }
    [JsonPropertyName("occurrences")] public long Occurrences { get; set; } = 1;
    /// <summary>Optional server-side fan-out target. The agent never sends SMTP credentials.</summary>
    [JsonPropertyName("recipient_email")] public string? RecipientEmail { get; set; }
}

public sealed class UpdateInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("mandatory")] public bool Mandatory { get; set; }
}
