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

    /// <summary>Audit fields excluded from the CONTENT checksum (batch identity):
    /// they change on every extraction and must never influence dedup or the
    /// deterministic batch ID. They ARE included in the uploaded payload.</summary>
    private static readonly string[] AuditFields =
        ["_sync_timestamp", "_sync_id", "source_last_seen_at"];

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

            // (1)+(2) write payload to a temp file, flush, close.
            // While writing, a CONTENT hash is accumulated over each row
            // serialized WITHOUT audit fields — so a re-extraction of identical
            // business data yields the same content checksum even though its
            // _sync_id/_sync_timestamp differ. The transport checksum (whole
            // gzip file) is computed separately in step (3).
            var tmpPath = Path.Combine(QueueDir, $"pending-{Guid.NewGuid():N}.tmp");
            long bytes;
            string payloadChecksum;
            string contentChecksum;
            using (var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
                {
                    foreach (var row in slice)
                    {
                        row["_company"] = company;    // stable — part of content identity

                        contentHash.AppendData(Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(WithoutAuditFields(row), JsonOpts)));
                        contentHash.AppendData("\n"u8.ToArray());

                        row["_sync_timestamp"] = syncTimestamp;  // audit-only
                        row["_sync_id"] = syncId;                // audit-only
                        writer.WriteLine(JsonSerializer.Serialize(row, JsonOpts));
                    }
                }
                contentChecksum = Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant();
            }

            // (3) transport checksum of the final bytes (server-side integrity check)
            using (var fs = File.OpenRead(tmpPath))
            {
                payloadChecksum = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                bytes = fs.Length;
            }

            // Before allocating a sequence, suppress an equivalent active batch.
            // Content-based: an identical re-extraction matches even with a new
            // sync id/timestamp, so it cannot mint a second batch.
            var existing = queue.FindEquivalentActiveBatch(
                dataset, company, windowFrom, windowTo, contentChecksum, slice.Count);
            if (existing is not null)
            {
                File.Delete(tmpPath);
                continue;
            }

            // (4) deterministic identity — stable content inputs only, no wall clock
            var seq = queue.NextSequence(dataset);
            var batchId = BatchIdentity.Compute(
                config.Cloud.AgentId, config.Cloud.CompanyId, dataset,
                windowFrom, windowTo, seq, contentChecksum);
            var finalPath = Path.Combine(QueueDir, batchId + ".ndjson.gz");

            // (5) atomic rename — or duplicate handling. Same id ⇒ same business
            // content (audit fields may differ), so if the final file already exists
            // (crash-replay of an identical extraction) the ORIGINAL payload is left
            // untouched — it may be mid-upload and its stored transport checksum
            // matches it — and only the redundant temp file is deleted.
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
                ChecksumSha256: payloadChecksum, SchemaVersion: AgentInfo.SchemaVersion,
                Status: "pending", RetryCount: 0, NextAttemptUtc: null, LastError: null,
                CreatedUtc: DateTime.UtcNow.ToString("O"),
                ContentChecksum: contentChecksum));

            if (enqueued) ids.Add(batchId);
            // duplicate ⇒ existing row already references this exact file — nothing to do
        }
        return ids;
    }

    /// <summary>Shallow copy of a row with audit fields removed (content identity).</summary>
    private static Row WithoutAuditFields(Row row)
    {
        var copy = new Row(row.Count);
        foreach (var (key, value) in row)
            if (Array.IndexOf(AuditFields, key) < 0)
                copy[key] = value;
        return copy;
    }

    /// <summary>Delete orphaned payload files with no matching queue row (crash debris).
    ///
    /// SAFETY: the referenced-file set comes from GetAllReferencedPayloadFileNames(),
    /// which reads EVERY queue row with no limit. (The previous implementation capped
    /// the set at 10k pending rows — a multi-day offline backlog exceeded the cap and
    /// startup then deleted live, still-queued payloads: permanent data loss.)
    /// If the referenced set cannot be read, NOTHING is deleted.</summary>
    public static int SweepOrphans(BatchQueueRepository queue, string? queueDir = null)
    {
        HashSet<string> live;
        try
        {
            live = queue.GetAllReferencedPayloadFileNames();
        }
        catch
        {
            return 0; // cannot establish what is referenced — delete nothing
        }

        var dir = queueDir ?? AgentInfo.QueueDir;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            var isTmp = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            var isPayload = name.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase);
            if (!isTmp && !isPayload) continue;
            if (isPayload && live.Contains(name)) continue;   // referenced — never touch

            // .tmp is always debris; unreferenced payloads only after a 1h grace
            // period (a batch may be mid-enqueue between rename and row insert).
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
            if (isTmp || age > TimeSpan.FromHours(1))
            {
                try { File.Delete(file); removed++; } catch { /* next sweep */ }
            }
        }
        return removed;
    }
}
