using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TallyAgent.Core.Data;

/// <summary>
/// Owns the SQLite database at %ProgramData%\TallyBigQueryAgent\agent.db.
/// WAL mode, versioned additive migrations, crash-safe. All repositories
/// borrow connections from here.
/// </summary>
public sealed class AgentDatabase
{
    public const int CurrentSchemaVersion = 8;

    private readonly string _connectionString;
    private readonly ILogger<AgentDatabase> _log;

    public AgentDatabase(ILogger<AgentDatabase> log, string? dbPath = null)
    {
        _log = log;
        AgentInfo.EnsureDirectories();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath ?? AgentInfo.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Match the PRAGMA busy timeout below. SQLite is shared by sync,
            // upload, heartbeat and UI readers; short write bursts should wait
            // rather than surface SQLITE_BUSY as an agent health failure.
            DefaultTimeout = 15,
        }.ToString();
        Initialize();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=15000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS schema_meta (
              key   TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            """);

        var version = GetSchemaVersion(conn, tx);
        if (version < 1) MigrateToV1(conn, tx);
        if (version < 2) MigrateToV2(conn, tx);
        if (version < 3) MigrateToV3(conn, tx);
        if (version < 4) MigrateToV4(conn, tx);
        if (version < 5) MigrateToV5(conn, tx);
        if (version < 6) MigrateToV6(conn, tx);
        if (version < 7) MigrateToV7(conn, tx);
        if (version < 8) MigrateToV8(conn, tx);
        // future: additive only

        Exec(conn, tx, """
            INSERT INTO schema_meta(key, value) VALUES('schema_version', $v)
              ON CONFLICT(key) DO UPDATE SET value=$v;
            INSERT INTO schema_meta(key, value) VALUES('agent_version', $av)
              ON CONFLICT(key) DO UPDATE SET value=$av;
            INSERT INTO schema_meta(key, value) VALUES('installed_at', $now)
              ON CONFLICT(key) DO NOTHING;
            """,
            ("$v", CurrentSchemaVersion.ToString()),
            ("$av", AgentInfo.Version),
            ("$now", DateTime.UtcNow.ToString("O")));

        tx.Commit();
        _log.LogInformation("SQLite ready at {Path} (schema v{Version})",
            AgentInfo.DatabasePath, CurrentSchemaVersion);
    }

    private static int GetSchemaVersion(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM schema_meta WHERE key='schema_version'";
        return cmd.ExecuteScalar() is string s && int.TryParse(s, out var v) ? v : 0;
    }

    private static void MigrateToV1(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS sync_checkpoints (
              dataset          TEXT NOT NULL,
              company          TEXT NOT NULL,
              last_from_date   TEXT,
              last_to_date     TEXT,
              last_alter_id    INTEGER,
              last_success_utc TEXT,
              full_sync_done   INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (dataset, company)
            );

            CREATE TABLE IF NOT EXISTS upload_batches (
              batch_id          TEXT PRIMARY KEY,
              dataset           TEXT NOT NULL,
              company           TEXT NOT NULL,
              sequence_no       INTEGER NOT NULL,
              sync_id           TEXT NOT NULL,
              extract_start_utc TEXT NOT NULL,
              extract_end_utc   TEXT NOT NULL,
              window_from       TEXT,
              window_to         TEXT,
              record_count      INTEGER NOT NULL,
              payload_path      TEXT NOT NULL,
              payload_bytes     INTEGER NOT NULL,
              checksum_sha256   TEXT NOT NULL,
              schema_version    TEXT NOT NULL,
              status            TEXT NOT NULL DEFAULT 'pending',
              retry_count       INTEGER NOT NULL DEFAULT 0,
              next_attempt_utc  TEXT,
              last_error        TEXT,
              created_utc       TEXT NOT NULL,
              acked_utc         TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_batches_status  ON upload_batches(status, next_attempt_utc);
            CREATE INDEX IF NOT EXISTS ix_batches_dataset ON upload_batches(dataset, sequence_no);

            CREATE TABLE IF NOT EXISTS batch_history (
              batch_id      TEXT PRIMARY KEY,
              dataset       TEXT NOT NULL,
              record_count  INTEGER NOT NULL,
              status        TEXT NOT NULL,
              created_utc   TEXT NOT NULL,
              completed_utc TEXT NOT NULL,
              retry_count   INTEGER NOT NULL
            );
            -- sequence_no is added by MigrateToV2 (kept out of V1 so pre-existing
            -- V1 databases and fresh databases take the identical migration path)

            CREATE TABLE IF NOT EXISTS sync_runs (
              sync_id       TEXT PRIMARY KEY,
              mode          TEXT NOT NULL,
              started_utc   TEXT NOT NULL,
              finished_utc  TEXT,
              status        TEXT NOT NULL,
              datasets_json TEXT,
              rows_total    INTEGER DEFAULT 0,
              error_message TEXT
            );

            CREATE TABLE IF NOT EXISTS error_log (
              id          INTEGER PRIMARY KEY AUTOINCREMENT,
              ts_utc      TEXT NOT NULL,
              category    TEXT NOT NULL,
              severity    TEXT NOT NULL,
              message     TEXT NOT NULL,
              stack_trace TEXT,
              operation   TEXT,
              dataset     TEXT,
              batch_id    TEXT,
              retry_count INTEGER DEFAULT 0,
              reported    INTEGER NOT NULL DEFAULT 0,
              group_key   TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_error_group ON error_log(group_key, ts_utc);
            CREATE INDEX IF NOT EXISTS ix_error_ts    ON error_log(ts_utc);

            CREATE TABLE IF NOT EXISTS heartbeat_history (
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              ts_utc       TEXT NOT NULL,
              delivered    INTEGER NOT NULL DEFAULT 0,
              payload_json TEXT NOT NULL
            );
            """);
    }

    /// <summary>V2: batch_history carries sequence_no so per-dataset sequence
    /// numbers stay monotonic after acked rows leave upload_batches.</summary>
    private static void MigrateToV2(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, """
            ALTER TABLE batch_history ADD COLUMN sequence_no INTEGER NOT NULL DEFAULT 0;
            """);
    }

    /// <summary>V3: content_checksum — SHA-256 over business rows excluding audit fields.</summary>
    private static void MigrateToV3(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, """
            ALTER TABLE upload_batches ADD COLUMN content_checksum TEXT NOT NULL DEFAULT '';
            """);
    }

    /// <summary>V4: window_coverage — durable per-window extraction evidence
    /// (requested window, actual min/max dates, records, run id, status).</summary>
    private static void MigrateToV4(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS window_coverage (
              id            INTEGER PRIMARY KEY AUTOINCREMENT,
              run_id        TEXT NOT NULL,
              dataset       TEXT NOT NULL,
              window_from   TEXT NOT NULL,
              window_to     TEXT NOT NULL,
              records       INTEGER NOT NULL,
              min_date      TEXT,
              max_date      TEXT,
              status        TEXT NOT NULL,
              completed_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_coverage_ds ON window_coverage(dataset, window_from);
            """);
    }

    /// <summary>V5: master_balances — last known computed balances per master
    /// (ledger opening/closing, stock item closing qty/value/rate), captured on
    /// the daily snapshot slot so every-cycle master exports never ask Tally to
    /// re-value the whole company yet still carry balance columns.</summary>
    private static void MigrateToV5(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS master_balances (
              dataset      TEXT NOT NULL,
              company      TEXT NOT NULL,
              guid         TEXT NOT NULL,
              values_json  TEXT NOT NULL,
              captured_utc TEXT NOT NULL,
              PRIMARY KEY (dataset, company, guid)
            );
            """);
    }

    /// <summary>V6: master_content_hashes — the content hash of the last master
    /// extraction whose upload the cloud actually ACKNOWLEDGED, per
    /// (dataset, company). An extraction whose hash matches is not enqueued
    /// again, which is what stops ~10,757 unchanged master rows being re-uploaded
    /// every cycle.
    ///
    /// A hash becomes "confirmed" only when every batch it produced is acked;
    /// until then it sits in pending_hash / pending_batches. That ordering is the
    /// whole safety property: recording the hash at enqueue time would let a
    /// batch that later fails permanently suppress every future upload of that
    /// dataset — masters silently not uploading, which nothing would notice.</summary>
    private static void MigrateToV6(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS master_content_hashes (
              dataset         TEXT NOT NULL,
              company         TEXT NOT NULL,
              confirmed_hash  TEXT,
              confirmed_utc   TEXT,
              pending_hash    TEXT,
              pending_batches TEXT,
              pending_utc     TEXT,
              PRIMARY KEY (dataset, company)
            );
            """);
    }

    /// <summary>V7: normalise sync_checkpoints.company. Untrimmed or
    /// differently-cased values orphan a history walk, because the reader and
    /// the writer then disagree about the key. Duplicates that collide once
    /// trimmed are collapsed, keeping the row that actually got furthest.</summary>
    private static void MigrateToV7(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, """
            DELETE FROM sync_checkpoints WHERE rowid NOT IN (
              SELECT rowid FROM (
                SELECT rowid, ROW_NUMBER() OVER (
                  PARTITION BY dataset, TRIM(LOWER(company))
                  ORDER BY full_sync_done DESC, last_success_utc DESC
                ) AS rn
                FROM sync_checkpoints
              ) WHERE rn = 1
            );
            UPDATE sync_checkpoints SET company = TRIM(company)
             WHERE company <> TRIM(company);
            """);
    }

    /// <summary>V8: sync_runs records what a run actually DID. Before this it
    /// held a status and a row count, so a successful run left no usable trace
    /// and "did last night work, and over what window?" was unanswerable.</summary>
    private static void MigrateToV8(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, """
            ALTER TABLE sync_runs ADD COLUMN window_from TEXT;
            ALTER TABLE sync_runs ADD COLUMN window_to TEXT;
            ALTER TABLE sync_runs ADD COLUMN datasets_attempted INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE sync_runs ADD COLUMN datasets_succeeded INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE sync_runs ADD COLUMN records_queued INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE sync_runs ADD COLUMN datasets_failed TEXT;
            """);
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string name, object value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
