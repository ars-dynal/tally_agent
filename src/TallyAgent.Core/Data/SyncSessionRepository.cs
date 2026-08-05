using Microsoft.Data.Sqlite;

namespace TallyAgent.Core.Data;

public sealed record SyncSession(
    string SyncId, string Company, string SyncType, string Status,
    string StartedUtc, string? ExtractionCompletedUtc, string? ReadyUtc,
    long RowsExtracted, long BatchesQueued, long BatchesAcknowledged,
    int FailedBatches, string? ErrorMessage);

/// <summary>
/// Durable production sync-session state. A FULL session remains active across
/// service restarts. Scheduled incremental sessions are blocked until a FULL
/// session reaches READY_FOR_CLOUD_VALIDATION.
/// </summary>
public sealed class SyncSessionRepository(AgentDatabase db)
{
    public const string Created = "CREATED";
    public const string Extracting = "EXTRACTING";
    public const string Uploading = "UPLOADING";
    public const string Ready = "READY_FOR_CLOUD_VALIDATION";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";

    public SyncSessionRepository : this(db)
    {
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS production_sync_sessions (
              sync_id                  TEXT PRIMARY KEY,
              company                  TEXT NOT NULL,
              sync_type                TEXT NOT NULL CHECK(sync_type IN ('full','incremental')),
              status                   TEXT NOT NULL,
              started_utc              TEXT NOT NULL,
              extraction_completed_utc TEXT,
              ready_utc                TEXT,
              rows_extracted           INTEGER NOT NULL DEFAULT 0,
              batches_queued           INTEGER NOT NULL DEFAULT 0,
              batches_acknowledged     INTEGER NOT NULL DEFAULT 0,
              failed_batches           INTEGER NOT NULL DEFAULT 0,
              error_message            TEXT,
              force_requested          INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_prod_sessions_company
              ON production_sync_sessions(company, started_utc DESC);
            CREATE TABLE IF NOT EXISTS production_session_acks (
              batch_id      TEXT PRIMARY KEY,
              sync_id       TEXT NOT NULL,
              acknowledged_utc TEXT NOT NULL,
              FOREIGN KEY(sync_id) REFERENCES production_sync_sessions(sync_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public bool HasReadyFull(string company)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
              SELECT 1 FROM production_sync_sessions
              WHERE company=$c AND sync_type='full' AND status=$ready)
            """;
        cmd.Parameters.AddWithValue("$c", company);
        cmd.Parameters.AddWithValue("$ready", Ready);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    public SyncSession StartOrResume(string company, string syncType, bool forceNew = false)
    {
        if (syncType == "full" && !forceNew)
        {
            var active = GetActiveFull(company);
            if (active is not null) return active;
        }

        var id = $"{(syncType == "full" ? "full" : "inc")}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..31];
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO production_sync_sessions
              (sync_id,company,sync_type,status,started_utc,force_requested)
            VALUES ($id,$c,$t,$s,$now,$force)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$c", company);
        cmd.Parameters.AddWithValue("$t", syncType);
        cmd.Parameters.AddWithValue("$s", Extracting);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$force", forceNew ? 1 : 0);
        cmd.ExecuteNonQuery();
        return Get(id)!;
    }

    public SyncSession? GetActiveFull(string company)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM production_sync_sessions
            WHERE company=$c AND sync_type='full'
              AND status IN ($created,$extracting,$uploading)
            ORDER BY started_utc DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$c", company);
        cmd.Parameters.AddWithValue("$created", Created);
        cmd.Parameters.AddWithValue("$extracting", Extracting);
        cmd.Parameters.AddWithValue("$uploading", Uploading);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public SyncSession? Get(string syncId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM production_sync_sessions WHERE sync_id=$id";
        cmd.Parameters.AddWithValue("$id", syncId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public void MarkExtractionCompleted(string syncId, long rows, long batchesQueued, int failedBatches, string? error)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE production_sync_sessions SET
              extraction_completed_utc=$now,
              rows_extracted=$rows,
              batches_queued=$batches,
              failed_batches=$failed,
              error_message=$err,
              status=CASE WHEN $failed > 0 THEN $failedStatus ELSE $uploading END
            WHERE sync_id=$id
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$rows", rows);
        cmd.Parameters.AddWithValue("$batches", batchesQueued);
        cmd.Parameters.AddWithValue("$failed", failedBatches);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$failedStatus", Failed);
        cmd.Parameters.AddWithValue("$uploading", Uploading);
        cmd.Parameters.AddWithValue("$id", syncId);
        cmd.ExecuteNonQuery();
        TryPromoteReady(syncId);
    }

    public void RecordBatchAcknowledged(string syncId, string batchId)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR IGNORE INTO production_session_acks(batch_id,sync_id,acknowledged_utc)
                VALUES($b,$s,$now)
                """;
            ins.Parameters.AddWithValue("$b", batchId);
            ins.Parameters.AddWithValue("$s", syncId);
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE production_sync_sessions
                SET batches_acknowledged=(SELECT COUNT(*) FROM production_session_acks WHERE sync_id=$s)
                WHERE sync_id=$s
                """;
            upd.Parameters.AddWithValue("$s", syncId);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        TryPromoteReady(syncId);
    }

    public void MarkFailed(string syncId, string error)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE production_sync_sessions SET status=$s,error_message=$e WHERE sync_id=$id";
        cmd.Parameters.AddWithValue("$s", Failed);
        cmd.Parameters.AddWithValue("$e", error.Length <= 2000 ? error : error[..2000]);
        cmd.Parameters.AddWithValue("$id", syncId);
        cmd.ExecuteNonQuery();
    }

    private void TryPromoteReady(string syncId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE production_sync_sessions SET status=$ready, ready_utc=$now
            WHERE sync_id=$id
              AND status=$uploading
              AND extraction_completed_utc IS NOT NULL
              AND failed_batches=0
              AND batches_acknowledged >= batches_queued
            """;
        cmd.Parameters.AddWithValue("$ready", Ready);
        cmd.Parameters.AddWithValue("$uploading", Uploading);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", syncId);
        cmd.ExecuteNonQuery();
    }

    private static SyncSession Map(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("sync_id")),
        r.GetString(r.GetOrdinal("company")),
        r.GetString(r.GetOrdinal("sync_type")),
        r.GetString(r.GetOrdinal("status")),
        r.GetString(r.GetOrdinal("started_utc")),
        r.IsDBNull(r.GetOrdinal("extraction_completed_utc")) ? null : r.GetString(r.GetOrdinal("extraction_completed_utc")),
        r.IsDBNull(r.GetOrdinal("ready_utc")) ? null : r.GetString(r.GetOrdinal("ready_utc")),
        r.GetInt64(r.GetOrdinal("rows_extracted")),
        r.GetInt64(r.GetOrdinal("batches_queued")),
        r.GetInt64(r.GetOrdinal("batches_acknowledged")),
        r.GetInt32(r.GetOrdinal("failed_batches")),
        r.IsDBNull(r.GetOrdinal("error_message")) ? null : r.GetString(r.GetOrdinal("error_message")));
}
