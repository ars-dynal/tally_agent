using System.IO.Compression;
using System.Text.Json;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Security;

namespace TallyAgent.Core.Diagnostics;

/// <summary>
/// Builds the sanitised diagnostic ZIP: config with secrets masked, recent logs,
/// queue statistics, checkpoints, recent errors, system info. Never includes
/// tokens, webhook URLs, or voucher payload data.
/// </summary>
public sealed class DiagnosticsExporter(
    AgentConfig config,
    BatchQueueRepository queue,
    ErrorLogRepository errors,
    CheckpointRepository checkpoints)
{
    public string Export(string? outputDir = null)
    {
        outputDir ??= Path.Combine(AgentInfo.DataDir, "diagnostics");
        Directory.CreateDirectory(outputDir);
        var zipPath = Path.Combine(outputDir,
            $"TallyAgent-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        AddText(zip, "config.sanitized.json", SanitizedConfig());
        AddText(zip, "system.json", JsonSerializer.Serialize(new
        {
            agent_version = AgentInfo.Version,
            schema_version = AgentInfo.SchemaVersion,
            machine = Environment.MachineName,
            windows = SystemInfo.WindowsVersion(),
            dotnet = Environment.Version.ToString(),
            disk_free_mb = SystemInfo.DiskFreeMb(),
            memory_mb = SystemInfo.ProcessMemoryMb(),
            network_available = SystemInfo.NetworkAvailable(),
            exported_utc = DateTime.UtcNow.ToString("O"),
        }, new JsonSerializerOptions { WriteIndented = true }));

        var stats = queue.GetStats();
        AddText(zip, "queue-stats.json", JsonSerializer.Serialize(new
        {
            pending = stats.Pending,
            failed = stats.Failed,
            acked_today = stats.AckedToday,
            queue_bytes = stats.TotalQueueBytes,
            failed_batches = queue.ListByStatus("failed", 50)
                .Select(b => new { b.BatchId, b.Dataset, b.RecordCount, b.RetryCount,
                                   LastError = SecretMasker.Scrub(b.LastError ?? "") }),
        }, new JsonSerializerOptions { WriteIndented = true }));

        // Sync progress (the class contract promises checkpoints in the ZIP;
        // this also keeps the primary-constructor parameter in use).
        AddText(zip, "checkpoints.json", JsonSerializer.Serialize(
            checkpoints.All(), new JsonSerializerOptions { WriteIndented = true }));

        AddText(zip, "recent-errors.json", JsonSerializer.Serialize(
            errors.Recent(100).Select(e => new
            {
                e.TsUtc, e.Category, e.Severity,
                Message = SecretMasker.Scrub(e.Message),
                e.Operation, e.Dataset, e.BatchId, e.RetryCount,
            }), new JsonSerializerOptions { WriteIndented = true }));

        // Recent log files (already secret-scrubbed at write time; cap 5 files / 5 MB each)
        if (Directory.Exists(AgentInfo.LogsDir))
        {
            foreach (var file in Directory.EnumerateFiles(AgentInfo.LogsDir, "*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
            {
                try
                {
                    var entry = zip.CreateEntry("logs/" + Path.GetFileName(file));
                    using var es = entry.Open();
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    var cap = Math.Min(fs.Length, 5 * 1024 * 1024);
                    fs.Seek(-cap, SeekOrigin.End);
                    fs.CopyTo(es);
                }
                catch { /* live log locked — skip */ }
            }
        }

        return zipPath;
    }

    private string SanitizedConfig()
    {
        var clone = JsonSerializer.Deserialize<AgentConfig>(JsonSerializer.Serialize(config))!;
        clone.Cloud.ApiToken = SecretMasker.MaskSecret(ConfigStore.GetApiToken(config));
        clone.Notifications.ErrorWebhookUrl =
            string.IsNullOrEmpty(ConfigStore.GetErrorWebhook(config)) ? "" : "(set — masked)";
        clone.Notifications.GoogleChatWebhookUrl =
            string.IsNullOrEmpty(ConfigStore.GetGoogleChatWebhook(config)) ? "" : "(set — masked)";
        clone.Notifications.SlackWebhookUrl =
            string.IsNullOrEmpty(ConfigStore.GetSlackWebhook(config)) ? "" : "(set — masked)";
        return JsonSerializer.Serialize(clone, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
