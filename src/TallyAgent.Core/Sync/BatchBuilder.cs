using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;

namespace TallyAgent.Core.Sync;

using Row = Dictionary<string, object?>;

/// <summary>
/// Serializes extracted rows to gzip-compressed NDJSON files under queue\ and
/// enqueues durable batch records with DETERMINISTIC batch IDs (§9.2).
///
/// Per-slice ordering (§6.1 steps 1–6; checkpoint advance is the caller's step 7):
///   1. write payload to a temp file    2. flush + close
///   3. compute SHA-256                 4. derive batch_id from stable inputs
///   5. atomic rename to {batch_id}.ndjson.gz
///   6. insert the queue row (idempotent: a byte-identical re-extraction of the
///      same window/sequence produces the SAME id and is silently skipped)
/// </summary>
public sealed class BatchBuilder(BatchQueueRepository queue, AgentConfig config,
    string? queueDirOverride = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private string QueueDir => queueDirOverride ?? AgentInfo.QueueDir;

    /// <summary>Split rows into batches of maxRecords and enqueue each.
    /// Returns the batch ids actually enqueued (duplicates excluded).
    /// <paramref name="syncTimestampOverride"/> exists for deterministic tests only;
    /// production callers omit it.</summary>
    public List<string> BuildAndEnqueue(
        string dataset, string company, string syncId, List<Row> rows,
        DateTime extractStartUtc, DateTime extractEndUtc,
        string? windowFrom, string? windowTo, int maxRecords,
        string? syncTimestampOverride = null)
    {
        var ids = new List<string>();
        if (rows.Count == 0) return ids;
        Directory.CreateDirectory(QueueDir);

        var syncTimestamp = syncTimestampOverride
            ?? DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        for (var offset = 0; offset < rows.Count; offset += maxRecords)
        {
            var slice = rows.Skip(offset).Take(maxRecords).ToList();

            // (1)+(2) write payload to a temp file, flush, close
            var tmpPath = Path.Combine(QueueDir, $"pending-{Guid.NewGuid():N}.tmp");
            long bytes;
            string checksum;
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                foreach (var row in slice)
                {
                    row["_sync_timestamp"] = syncTimestamp;
                    row["_sync_id"] = syncId;      // audit-only — never key material
                    row["_company"] = company;
                    writer.WriteLine(JsonSerializer.Serialize(row, JsonOpts));
                }
            }

            // (3) checksum of the final bytes
            using (var fs = File.OpenRead(tmpPath))
            {
                checksum = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                bytes = fs.Length;
            }

            // Before allocating a sequence, suppress an equivalent active batch.
            // Otherwise an identical retry would receive the next sequence number,
            // produce a different batch ID, and create a duplicate queue entry.
            var existing = queue.FindEquivalentActiveBatch(
                dataset, company, windowFrom, windowTo, checksum, slice.Count);
            if (existing is not null)
            {
                File.Delete(tmpPath);
                continue;
            }

            // (4) deterministic identity — stable inputs only, no wall clock
            var seq = queue.NextSequence(dataset);
            var batchId = BatchIdentity.Compute(
                config.Cloud.AgentId, config.Cloud.CompanyId, dataset,
                windowFrom, windowTo, seq, checksum);
            var finalPath = Path.Combine(QueueDir, batchId + ".ndjson.gz");

            // (5) atomic rename — or duplicate handling. Same id ⇒ same bytes by
            // construction, so if the final file already exists (crash-replay of an
            // identical extraction) the ORIGINAL payload is left untouched — it may
            // be mid-upload — and only the redundant temp file is deleted.
            if (File.Exists(finalPath))
                File.Delete(tmpPath);
            else
                File.Move(tmpPath, finalPath);

            // (6) durable queue row — idempotent on batch_id
            var enqueued = queue.TryEnqueue(new QueuedBatch(
                BatchId: batchId, Dataset: dataset, Company: company, SequenceNo: seq,
                SyncId: syncId,
                ExtractStartUtc: extractStartUtc.ToString("O"),
                ExtractEndUtc: extractEndUtc.ToString("O"),
                WindowFrom: windowFrom, WindowTo: windowTo,
                RecordCount: slice.Count, PayloadPath: finalPath, PayloadBytes: bytes,
                ChecksumSha256: checksum, SchemaVersion: AgentInfo.SchemaVersion,
                Status: "pending", RetryCount: 0, NextAttemptUtc: null, LastError: null,
                CreatedUtc: DateTime.UtcNow.ToString("O")));

            if (enqueued) ids.Add(batchId);
            // duplicate ⇒ existing row already references this exact file — nothing to do
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
