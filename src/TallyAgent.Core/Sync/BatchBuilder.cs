using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TallyAgent.Core.Data;

namespace TallyAgent.Core.Sync;

using Row = Dictionary<string, object?>;

/// <summary>
/// Serializes extracted rows to gzip-compressed NDJSON files under queue\,
/// appends sync meta columns, computes SHA-256, and enqueues durable batch
/// records. Writes are torn-write-safe (temp file + atomic move) and the queue
/// row is only inserted after the payload file is fully on disk.
/// </summary>
public sealed class BatchBuilder(BatchQueueRepository queue)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Split rows into batches of maxRecords and enqueue each. Returns batch ids.</summary>
    public List<string> BuildAndEnqueue(
        string dataset, string company, string syncId, List<Row> rows,
        DateTime extractStartUtc, DateTime extractEndUtc,
        string? windowFrom, string? windowTo, int maxRecords)
    {
        var ids = new List<string>();
        if (rows.Count == 0) return ids;

        var syncTimestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        for (var offset = 0; offset < rows.Count; offset += maxRecords)
        {
            var slice = rows.Skip(offset).Take(maxRecords).ToList();
            var seq = queue.NextSequence(dataset);
            var batchId = $"{dataset}-{DateTime.UtcNow:yyyyMMddHHmmss}-{seq:D6}";
            var finalPath = Path.Combine(AgentInfo.QueueDir, batchId + ".ndjson.gz");
            var tmpPath = finalPath + ".tmp";

            long bytes;
            string checksum;
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                foreach (var row in slice)
                {
                    row["_sync_timestamp"] = syncTimestamp;
                    row["_sync_id"] = syncId;
                    row["_company"] = company;
                    writer.WriteLine(JsonSerializer.Serialize(row, JsonOpts));
                }
            }

            using (var fs = File.OpenRead(tmpPath))
            {
                checksum = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                bytes = fs.Length;
            }

            File.Move(tmpPath, finalPath, overwrite: true);

            queue.Enqueue(new QueuedBatch(
                BatchId: batchId, Dataset: dataset, Company: company, SequenceNo: seq,
                SyncId: syncId,
                ExtractStartUtc: extractStartUtc.ToString("O"),
                ExtractEndUtc: extractEndUtc.ToString("O"),
                WindowFrom: windowFrom, WindowTo: windowTo,
                RecordCount: slice.Count, PayloadPath: finalPath, PayloadBytes: bytes,
                ChecksumSha256: checksum, SchemaVersion: AgentInfo.SchemaVersion,
                Status: "pending", RetryCount: 0, NextAttemptUtc: null, LastError: null,
                CreatedUtc: DateTime.UtcNow.ToString("O")));
            ids.Add(batchId);
        }
        return ids;
    }

    /// <summary>Delete orphaned payload files with no matching queue row (crash debris).</summary>
    public static int SweepOrphans(BatchQueueRepository queue)
    {
        var live = queue.ListByStatus("pending", 10000).Concat(queue.ListByStatus("uploading", 1000))
            .Concat(queue.ListByStatus("failed", 10000))
            .Select(b => Path.GetFileName(b.PayloadPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(AgentInfo.QueueDir))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                (!live.Contains(name) && name.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase)))
            {
                // .tmp files are always debris; .ndjson.gz only if unreferenced AND older than 1h
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || age > TimeSpan.FromHours(1))
                {
                    try { File.Delete(file); removed++; } catch { /* next sweep */ }
                }
            }
        }
        return removed;
    }
}
