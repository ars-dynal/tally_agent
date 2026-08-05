using Microsoft.Data.Sqlite;

namespace TallyAgent.Core.Data;

public sealed record QueuedBatch(
    string BatchId, string Dataset, string Company, long SequenceNo, string SyncId,
    string ExtractStartUtc, string ExtractEndUtc, string? WindowFrom, string? WindowTo,
    long RecordCount, string PayloadPath, long PayloadBytes, string ChecksumSha256,
    string SchemaVersion, string Status, int RetryCount, string? NextAttemptUtc,
    string? LastError, string CreatedUtc, string ContentChecksum = "");

public sealed record QueueStats(long Pending, long Failed, long AckedToday, long TotalQueueBytes);

/// <summary>Durable upload queue. Payloads are .ndjson.gz files under queue\; this
/// table stores metadata + state machine: pending → uploading → acked | failed.</summary>
public sealed class BatchQueueRepository(AgentDatabase db)
{
    public void Enqueue(QueuedBatch b)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO upload_batches
              (batch_id, dataset, company, sequence_no, sync_id, extract_start_utc, extract_end_utc,
               window_from, window_to, record_count, payload_path, payload_bytes, checksum_sha256,
               schema_version, status, retry_count, created_utc, content_checksum)
            VALUES ($id,$ds,$co,$seq,$sync,$es,$ee,$wf,$wt,$rc,$pp,$pb,$ck,$sv,'pending',0,$cr,$cc)
            """;
        cmd.Parameters.AddWithValue("$cc", b.ContentChecksum);
        cmd.Parameters.AddWithValue("$id", b.BatchId);
        cmd.Parameters.AddWithValue("$ds", b.Dataset);
        cmd.Parameters.AddWithValue("$co", b.Company);
        cmd.Parameters.AddWithValue("$seq", b.SequenceNo);
        cmd.Parameters.AddWithValue("$sync", b.SyncId);
        cmd.Parameters.AddWithValue("$es", b.ExtractStartUtc);
        cmd.Parameters.AddWithValue("$ee", b.ExtractEndUtc);
        cmd.Parameters.AddWithValue("$wf", (object?)b.WindowFrom ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wt", (object?)b.WindowTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rc", b.RecordCount);
        cmd.Parameters.AddWithValue("$pp", b.PayloadPath);
        cmd.Parameters.AddWithValue("$pb", b.PayloadBytes);
        cmd.Parameters.AddWithValue("$ck", b.ChecksumSha256);
        cmd.Parameters.AddWithValue("$sv", b.SchemaVersion);
        cmd.Parameters.AddWithValue("$cr", b.CreatedUtc);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Next monotonic sequence number for a dataset. Considers BOTH the
    /// live queue and completed history — acked rows leave upload_batches, and a
    /// sequence regression would corrupt deterministic batch IDs.</summary>
    public long NextSequence(string dataset)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT MAX(seq)+1 FROM (
              SELECT COALESCE(MAX(sequence_no),0) AS seq FROM upload_batches WHERE dataset=$ds
              UNION ALL
              SELECT COALESCE(MAX(sequence_no),0) AS seq FROM batch_history  WHERE dataset=$ds
            )
            """;
        cmd.Parameters.AddWithValue("$ds", dataset);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Find an equivalent active batch before allocating a new sequence.
    /// This prevents an identical re-extraction from receiving a different sequence
    /// number and therefore a different deterministic batch ID.</summary>
    public QueuedBatch? FindEquivalentActiveBatch(
        string dataset, string company, string? windowFrom, string? windowTo,
        string contentChecksum, long recordCount)
    {
        // Matches on content_checksum (business rows only, audit fields excluded)
        // so a re-extraction with a new _sync_id/_sync_timestamp still matches.
        // Rows migrated from schema v2 have '' and never match — safe.
        if (string.IsNullOrEmpty(contentChecksum)) return null;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM upload_batches
            WHERE dataset=$ds
              AND company=$co
              AND (($wf IS NULL AND window_from IS NULL) OR window_from=$wf)
              AND (($wt IS NULL AND window_to IS NULL) OR window_to=$wt)
              AND content_checksum=$ck
              AND record_count=$rc
              AND status IN ('pending','uploading','failed')
            ORDER BY created_utc ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ds", dataset);
        cmd.Parameters.AddWithValue("$co", company);
        cmd.Parameters.AddWithValue("$wf", (object?)windowFrom ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wt", (object?)windowTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ck", contentChecksum);
        cmd.Parameters.AddWithValue("$rc", recordCount);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>Idempotent enqueue: returns false (without throwing) when a batch
    /// with the same deterministic batch_id already exists — i.e. a byte-identical
    /// re-extraction of the same window/sequence. The caller keeps the payload file
    /// (same name, same bytes) and skips the duplicate row.</summary>
    public bool TryEnqueue(QueuedBatch b)
    {
        try
        {
            Enqueue(b);
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            return false;
        }
    }

    /// <summary>Oldest due batch (per-dataset sequence order preserved). Marks it 'uploading'.</summary>
    public QueuedBatch? DequeueNextDue(DateTime nowUtc)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT * FROM upload_batches
            WHERE status IN ('pending','uploading')
              AND (next_attempt_utc IS NULL OR next_attempt_utc <= $now)
            ORDER BY created_utc ASC, sequence_no ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var batch = Map(r);
        r.Close();

        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE upload_batches SET status='uploading' WHERE batch_id=$id";
        upd.Parameters.AddWithValue("$id", batch.BatchId);
        upd.ExecuteNonQuery();
        tx.Commit();
        return batch;
    }

    /// <summary>Successful (or duplicate) ack: move to history, delete payload file.</summary>
    public void Ack(string batchId)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        string? payloadPath = null;

        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT payload_path FROM upload_batches WHERE batch_id=$id";
            sel.Parameters.AddWithValue("$id", batchId);
            payloadPath = sel.ExecuteScalar() as string;
        }

        using (var hist = conn.CreateCommand())
        {
            hist.Transaction = tx;
            hist.CommandText = """
                INSERT OR REPLACE INTO batch_history
                  (batch_id, dataset, record_count, status, created_utc,
                   completed_utc, retry_count, sequence_no)
                SELECT batch_id, dataset, record_count, 'acked', created_utc,
                       $now, retry_count, sequence_no
                FROM upload_batches WHERE batch_id=$id
                """;
            hist.Parameters.AddWithValue("$id", batchId);
            hist.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            hist.ExecuteNonQuery();
        }
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM upload_batches WHERE batch_id=$id";
            del.Parameters.AddWithValue("$id", batchId);
            del.ExecuteNonQuery();
        }
        tx.Commit();

        // Payload deleted only after the DB transaction committed the ack.
        if (payloadPath is not null && File.Exists(payloadPath))
        {
            try { File.Delete(payloadPath); } catch { /* orphan cleanup sweeps later */ }
        }
    }

    /// <summary>Transient failure: schedule retry with the supplied delay.</summary>
    public void ScheduleRetry(string batchId, string error, TimeSpan delay)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE upload_batches
            SET status='pending', retry_count=retry_count+1,
                next_attempt_utc=$next, last_error=$err
            WHERE batch_id=$id
            """;
        cmd.Parameters.AddWithValue("$next", DateTime.UtcNow.Add(delay).ToString("O"));
        cmd.Parameters.AddWithValue("$err", Truncate(error, 2000));
        cmd.Parameters.AddWithValue("$id", batchId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Permanent failure (schema rejection): parked for human attention, payload kept.</summary>
    public void MarkFailed(string batchId, string error)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE upload_batches SET status='failed', last_error=$err WHERE batch_id=$id
            """;
        cmd.Parameters.AddWithValue("$err", Truncate(error, 2000));
        cmd.Parameters.AddWithValue("$id", batchId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Requeue all failed batches (Manager "Retry failed batches").</summary>
    public int RetryAllFailed()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE upload_batches
            SET status='pending', next_attempt_utc=NULL, last_error=NULL
            WHERE status='failed'
            """;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Reset 'uploading' rows left behind by a crash back to 'pending'.</summary>
    public int RecoverStuckUploads()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE upload_batches SET status='pending' WHERE status='uploading'";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>ALL payload file names referenced by ANY queue row, regardless of
    /// status and with NO row limit. Used by the startup orphan sweep — an
    /// incomplete list here would cause live payload files to be deleted.</summary>
    public HashSet<string> GetAllReferencedPayloadFileNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_path FROM upload_batches";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = Path.GetFileName(r.GetString(0));
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }

    /// <summary>Mark 'failed' any queue row whose payload file is missing on disk.
    /// Returns the affected batch ids (startup integrity check).</summary>
    public List<string> MarkRowsWithMissingPayloads()
    {
        var missing = new List<string>();
        using var conn = db.Open();
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = """
                SELECT batch_id, payload_path FROM upload_batches
                WHERE status IN ('pending','uploading')
                """;
            using var r = sel.ExecuteReader();
            while (r.Read())
                if (!File.Exists(r.GetString(1)))
                    missing.Add(r.GetString(0));
        }
        foreach (var id in missing) MarkFailed(id, "Payload file missing at startup");
        return missing;
    }

    public QueueStats GetStats()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM upload_batches WHERE status IN ('pending','uploading')),
              (SELECT COUNT(*) FROM upload_batches WHERE status='failed'),
              (SELECT COUNT(*) FROM batch_history WHERE status='acked' AND completed_utc >= $today),
              (SELECT COALESCE(SUM(payload_bytes),0) FROM upload_batches)
            """;
        cmd.Parameters.AddWithValue("$today", DateTime.UtcNow.Date.ToString("O"));
        using var r = cmd.ExecuteReader();
        r.Read();
        return new QueueStats(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
    }

    public IReadOnlyList<QueuedBatch> ListByStatus(string status, int limit = 100)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM upload_batches WHERE status=$s ORDER BY created_utc LIMIT $n";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<QueuedBatch>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private static QueuedBatch Map(SqliteDataReader r) => new(
        BatchId: r.GetString(r.GetOrdinal("batch_id")),
        Dataset: r.GetString(r.GetOrdinal("dataset")),
        Company: r.GetString(r.GetOrdinal("company")),
        SequenceNo: r.GetInt64(r.GetOrdinal("sequence_no")),
        SyncId: r.GetString(r.GetOrdinal("sync_id")),
        ExtractStartUtc: r.GetString(r.GetOrdinal("extract_start_utc")),
        ExtractEndUtc: r.GetString(r.GetOrdinal("extract_end_utc")),
        WindowFrom: r.IsDBNull(r.GetOrdinal("window_from")) ? null : r.GetString(r.GetOrdinal("window_from")),
        WindowTo: r.IsDBNull(r.GetOrdinal("window_to")) ? null : r.GetString(r.GetOrdinal("window_to")),
        RecordCount: r.GetInt64(r.GetOrdinal("record_count")),
        PayloadPath: r.GetString(r.GetOrdinal("payload_path")),
        PayloadBytes: r.GetInt64(r.GetOrdinal("payload_bytes")),
        ChecksumSha256: r.GetString(r.GetOrdinal("checksum_sha256")),
        SchemaVersion: r.GetString(r.GetOrdinal("schema_version")),
        Status: r.GetString(r.GetOrdinal("status")),
        RetryCount: r.GetInt32(r.GetOrdinal("retry_count")),
        NextAttemptUtc: r.IsDBNull(r.GetOrdinal("next_attempt_utc")) ? null : r.GetString(r.GetOrdinal("next_attempt_utc")),
        LastError: r.IsDBNull(r.GetOrdinal("last_error")) ? null : r.GetString(r.GetOrdinal("last_error")),
        CreatedUtc: r.GetString(r.GetOrdinal("created_utc")),
        ContentChecksum: r.IsDBNull(r.GetOrdinal("content_checksum"))
            ? "" : r.GetString(r.GetOrdinal("content_checksum")));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
