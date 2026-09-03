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
    /// <summary>Optional ISO date (yyyy-MM-dd) at which the historical backfill
    /// STOPS. Set it together with extractionStartDate to walk exactly one
    /// financial year at a time (e.g. 2019-04-01 to 2020-03-31), so the range
    /// Tally is asked for is bounded by that year instead of the whole history.
    /// Blank (the default) means walk to today, which is what the machine
    /// tracking live data should use.</summary>
    [JsonPropertyName("extractionEndDate")] public string ExtractionEndDate { get; set; } = "";
    [JsonPropertyName("syncFrequencyMinutes")] public int SyncFrequencyMinutes { get; set; } = 15;
    [JsonPropertyName("requestTimeoutSeconds")] public int RequestTimeoutSeconds { get; set; } = 120;
    /// <summary>Timeout for windowed voucher extraction requests, which are the
    /// heaviest calls the agent makes. Defaults higher than requestTimeoutSeconds
    /// so a large month gets a fair chance before the window is split.</summary>
    [JsonPropertyName("voucherTimeoutSeconds")] public int VoucherTimeoutSeconds { get; set; } = 180;
    /// <summary>Timeout for full-financial-year snapshot reports (Trial Balance,
    /// Balance Sheet, P&amp;L, Stock Summary). These are never retried at the
    /// same size inside a cycle — a timeout simply defers the snapshot to the
    /// next snapshot slot.</summary>
    [JsonPropertyName("snapshotTimeoutSeconds")] public int SnapshotTimeoutSeconds { get; set; } = 300;
    /// <summary>Server-local hour (0-23) after which the once-daily snapshot
    /// reports (TB/BS/P&amp;L/Stock Summary/outstanding) are extracted. They are
    /// the heaviest thing the agent asks Tally for, so they run once per day
    /// (default 20:00 — after office hours), on the first ever run, and on a
    /// Force Full Sync. Set snapshotEveryCycle=true to restore v2.0.4 behaviour.</summary>
    [JsonPropertyName("snapshotHourLocal")] public int SnapshotHourLocal { get; set; } = 20;
    [JsonPropertyName("snapshotEveryCycle")] public bool SnapshotEveryCycle { get; set; } = false;
    /// <summary>Computed balances (ledger OPENING/CLOSINGBALANCE, stock item
    /// closing qty/value/rate) force Tally to walk every voucher for every
    /// ledger/item. By default (false) they are requested from Tally once a day
    /// on the snapshot slot and cached per GUID; every other master export
    /// fills the balance columns from that cache. Set true to ask Tally for
    /// fresh balances on EVERY cycle (v2.0.4 behaviour, heavy).</summary>
    [JsonPropertyName("includeMasterBalances")] public bool IncludeMasterBalances { get; set; } = false;
    /// <summary>Also request the legacy LEDGERENTRIES/INVENTORYENTRIES lists in
    /// the voucher export (older Tally builds that do not emit ALL*ENTRIES).
    /// Off by default: requesting both makes Tally serialize every line twice.</summary>
    [JsonPropertyName("voucherFetchLegacyLists")] public bool VoucherFetchLegacyLists { get; set; } = false;
    /// <summary>Idle gap after EVERY Tally request (masters, reports, probes and
    /// voucher windows alike) so the Tally UI thread gets a slice between
    /// requests. 0 disables.</summary>
    [JsonPropertyName("requestPauseSeconds")] public int RequestPauseSeconds { get; set; } = 2;
    /// <summary>How long an active sync waits for a temporarily unavailable Tally server before giving up.</summary>
    [JsonPropertyName("reconnectMaxMinutes")] public int ReconnectMaxMinutes { get; set; } = 30;
    /// <summary>Delay between Tally reachability probes while auto-reconnecting.</summary>
    [JsonPropertyName("reconnectRetrySeconds")] public int ReconnectRetrySeconds { get; set; } = 30;
    /// <summary>Pause between voucher window extractions so the Tally UI gets
    /// breathing room during a long history walk. Tally's XML server shares the
    /// application thread — while a request runs, operators feel it; the gap
    /// between requests is when the UI catches up. 0 disables the pause.</summary>
    [JsonPropertyName("windowPauseSeconds")] public int WindowPauseSeconds { get; set; } = 5;
    /// <summary>Kept for config compatibility. Tally's XML server is single-
    /// threaded and 2 in-flight requests are known to stall TallyPrime, so the
    /// effective concurrency is ALWAYS 1 (see EffectiveMaxConcurrentTallyRequests).</summary>
    [JsonPropertyName("maxConcurrentTallyRequests")] public int MaxConcurrentTallyRequests { get; set; } = 1;
    [JsonPropertyName("gateWaitSeconds")] public int GateWaitSeconds { get; set; } = 120;
    /// <summary>Total timeout/reconnect retries permitted per sync run across ALL
    /// datasets and windows — a stalling Tally ends the cycle instead of being
    /// hammered; the run resumes from checkpoints next cycle.</summary>
    [JsonPropertyName("maxRetriesPerRun")] public int MaxRetriesPerRun { get; set; } = 5;
    [JsonPropertyName("maxResponseMb")] public int MaxResponseMb { get; set; } = 256;

    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveMaxConcurrentTallyRequests => 1;
    [JsonPropertyName("autoDiscoverCompanies")] public bool AutoDiscoverCompanies { get; set; } = true;
    /// <summary>Snapshot reports (Trial Balance, Balance Sheet, P&amp;L, Stock
    /// Summary, outstanding payables/receivables). These are the heaviest
    /// requests the agent makes - each one asks Tally to compute a whole
    /// financial year - and a Force Full Sync runs all six. Turn them OFF on a
    /// machine doing a historical back-fill: the underlying vouchers and
    /// ledgers are extracted anyway, so the reports can be derived downstream,
    /// and skipping them removes the longest stalls from the walk.</summary>
    [JsonPropertyName("enableSnapshots")] public bool EnableSnapshots { get; set; } = true;
    /// <summary>Per-report override of <see cref="EnableSnapshots"/>, keyed by
    /// dataset name (balance_sheet, outstanding_payables, ...). Exists because
    /// the six snapshot reports are NOT equivalent: balance_sheet, profit_loss
    /// and stock_summary make Tally compute across the whole company and have
    /// been observed to hang tally.exe outright, while the two outstanding
    /// reports are needed for AR/AP reconciliation. With a single all-or-nothing
    /// flag the outstandings sit behind balance_sheet in run order and are never
    /// reached at all. A dataset with no entry here falls back to
    /// enableSnapshots, so an existing config file keeps its current behaviour;
    /// enableSnapshots=false still disables every report regardless of entries.</summary>
    [JsonPropertyName("snapshotDatasets")]
    public Dictionary<string, bool>? SnapshotDatasets { get; set; }
    /// <summary>Keep producing the legacy <c>vouchers</c> dataset, which is a
    /// byte-identical copy of <c>day_book</c> — VoucherExtractor adds the same
    /// row object to both. Measured at 170,073 vs 170,056 rows in raw for the
    /// same data. Off by default from v2.1.0; turn on only while something
    /// downstream still reads the <c>vouchers</c> name.</summary>
    [JsonPropertyName("emitLegacyVouchersDataset")]
    public bool EmitLegacyVouchersDataset { get; set; } = false;
    [JsonPropertyName("enableMasters")] public bool EnableMasters { get; set; } = true;
    [JsonPropertyName("enableVouchers")] public bool EnableVouchers { get; set; } = true;
    [JsonPropertyName("enableInventory")] public bool EnableInventory { get; set; } = true;
    [JsonPropertyName("enableGst")] public bool EnableGst { get; set; } = true;
    [JsonPropertyName("enableCostCentres")] public bool EnableCostCentres { get; set; } = true;
    [JsonPropertyName("incrementalLookbackDays")] public int IncrementalLookbackDays { get; set; } = 7;
    /// <summary>Initial voucher window size for a history walk. The engine
    /// shrinks windows adaptively (never grows them within a run) when a window
    /// times out or takes more than 60% of voucherTimeoutSeconds.</summary>
    [JsonPropertyName("fullSyncChunkDays")] public int FullSyncChunkDays { get; set; } = 7;

    /// <summary>Is this snapshot report enabled? A per-dataset entry wins over
    /// the blanket flag; an absent entry (or absent section) falls back to it.
    /// <see cref="EnableSnapshots"/> remains a master switch: false disables
    /// every report even where an entry says true.</summary>
    public bool IsSnapshotEnabled(string dataset)
    {
        if (!EnableSnapshots) return false;
        if (SnapshotDatasets is { Count: > 0 })
            foreach (var kv in SnapshotDatasets)
                if (string.Equals(kv.Key, dataset, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
        return true;
    }

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
