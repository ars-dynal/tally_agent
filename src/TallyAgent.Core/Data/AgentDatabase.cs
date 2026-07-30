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
    public const int CurrentSchemaVersion = 1;

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
            DefaultTimeout = 15,
        }.ToString();
        Initialize();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
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
        // future: if (version < 2) MigrateToV2(conn, tx); — additive only

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
