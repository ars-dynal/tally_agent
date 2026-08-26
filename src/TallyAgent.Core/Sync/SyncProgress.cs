using System.Text.Json;
using System.Text.Json.Serialization;

namespace TallyAgent.Core.Sync;

/// <summary>What the sync service is doing right now. The service writes this
/// to ProgramData so the Management Console - a separate process that cannot
/// see the service's memory - can show live progress instead of only an error
/// list. Writing is best effort: a failed write never affects a sync.</summary>
public sealed class SyncProgressSnapshot
{
    [JsonPropertyName("syncId")] public string SyncId { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("operation")] public string Operation { get; set; } = "idle";
    [JsonPropertyName("status")] public string Status { get; set; } = "idle";
    [JsonPropertyName("company")] public string Company { get; set; } = "";
    /// <summary>Configured extraction start (blank = start of this financial year).</summary>
    [JsonPropertyName("rangeFrom")] public string RangeFrom { get; set; } = "";
    /// <summary>Configured extraction end, or "today" when unbounded.</summary>
    [JsonPropertyName("rangeTo")] public string RangeTo { get; set; } = "";
    [JsonPropertyName("datasetsDone")] public int DatasetsDone { get; set; }
    [JsonPropertyName("datasetsTotal")] public int DatasetsTotal { get; set; }
    [JsonPropertyName("windowsDone")] public int WindowsDone { get; set; }
    [JsonPropertyName("windowsTotal")] public int WindowsTotal { get; set; }
    [JsonPropertyName("rows")] public long Rows { get; set; }
    [JsonPropertyName("startedUtc")] public string StartedUtc { get; set; } = "";
    [JsonPropertyName("updatedUtc")] public string UpdatedUtc { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

/// <summary>Reads and writes the progress snapshot at
/// C:\ProgramData\TallyBigQueryAgent\progress.json.</summary>
public static class SyncProgressStore
{
    public static string FilePath => Path.Combine(AgentInfo.DataDir, "progress.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };
    private static readonly object Gate = new();

    /// <summary>Publish the snapshot. Swallows every error - progress display
    /// is cosmetic and must never be able to fail a sync cycle.</summary>
    public static void Write(SyncProgressSnapshot snapshot)
    {
        try
        {
            snapshot.UpdatedUtc = DateTime.UtcNow.ToString("O");
            lock (Gate)
            {
                var final = FilePath;
                var tmp = final + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Opts));
                File.Move(tmp, final, overwrite: true);
            }
        }
        catch
        {
            // ignored on purpose
        }
    }

    /// <summary>Read the snapshot, or null when there is none / it is unreadable.</summary>
    public static SyncProgressSnapshot? Read()
    {
        try
        {
            var final = FilePath;
            if (!File.Exists(final)) return null;
            return JsonSerializer.Deserialize<SyncProgressSnapshot>(File.ReadAllText(final));
        }
        catch
        {
            return null;
        }
    }
}
