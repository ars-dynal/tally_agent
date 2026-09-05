namespace TallyAgent.Core.Data;

public sealed record SyncCheckpoint(
    string Dataset, string Company, string? LastFromDate, string? LastToDate,
    long? LastAlterId, string? LastSuccessUtc, bool FullSyncDone);

public sealed class CheckpointRepository(AgentDatabase db)
{
    public SyncCheckpoint? Get(string dataset, string company)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        // TRIM + NOCASE. SQLite's = is case-sensitive and does not ignore
        // whitespace, so a config edit changing the case or padding of the
        // company name silently orphans an entire history walk - the writer
        // stores under one key and the reader looks under another.
        cmd.CommandText =
            "SELECT * FROM sync_checkpoints " +
            "WHERE dataset=$d AND TRIM(company)=TRIM($c) COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$d", dataset);
        cmd.Parameters.AddWithValue("$c", company);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SyncCheckpoint(
            r.GetString(r.GetOrdinal("dataset")),
            r.GetString(r.GetOrdinal("company")),
            r.IsDBNull(r.GetOrdinal("last_from_date")) ? null : r.GetString(r.GetOrdinal("last_from_date")),
            r.IsDBNull(r.GetOrdinal("last_to_date")) ? null : r.GetString(r.GetOrdinal("last_to_date")),
            r.IsDBNull(r.GetOrdinal("last_alter_id")) ? null : r.GetInt64(r.GetOrdinal("last_alter_id")),
            r.IsDBNull(r.GetOrdinal("last_success_utc")) ? null : r.GetString(r.GetOrdinal("last_success_utc")),
            r.GetInt64(r.GetOrdinal("full_sync_done")) == 1);
    }

    /// <summary>All checkpoints (diagnostics export).</summary>
    public List<SyncCheckpoint> All()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM sync_checkpoints ORDER BY dataset, company";
        using var r = cmd.ExecuteReader();
        var rows = new List<SyncCheckpoint>();
        while (r.Read())
            rows.Add(new SyncCheckpoint(
                r.GetString(r.GetOrdinal("dataset")),
                r.GetString(r.GetOrdinal("company")),
                r.IsDBNull(r.GetOrdinal("last_from_date")) ? null : r.GetString(r.GetOrdinal("last_from_date")),
                r.IsDBNull(r.GetOrdinal("last_to_date")) ? null : r.GetString(r.GetOrdinal("last_to_date")),
                r.IsDBNull(r.GetOrdinal("last_alter_id")) ? null : r.GetInt64(r.GetOrdinal("last_alter_id")),
                r.IsDBNull(r.GetOrdinal("last_success_utc")) ? null : r.GetString(r.GetOrdinal("last_success_utc")),
                r.GetInt64(r.GetOrdinal("full_sync_done")) == 1));
        return rows;
    }

    public void Upsert(SyncCheckpoint cp)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_checkpoints
              (dataset, company, last_from_date, last_to_date, last_alter_id, last_success_utc, full_sync_done)
            VALUES ($d,$c,$f,$t,$a,$s,$done)
            ON CONFLICT(dataset, company) DO UPDATE SET
              last_from_date=$f, last_to_date=$t, last_alter_id=$a,
              last_success_utc=$s, full_sync_done=$done
            """;
        cmd.Parameters.AddWithValue("$d", cp.Dataset);
        // Normalised on the way in so the stored key cannot drift.
        cmd.Parameters.AddWithValue("$c", cp.Company.Trim());
        cmd.Parameters.AddWithValue("$f", (object?)cp.LastFromDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)cp.LastToDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", (object?)cp.LastAlterId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)cp.LastSuccessUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$done", cp.FullSyncDone ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public string? GetLastSuccessfulSyncUtc()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(last_success_utc) FROM sync_checkpoints";
        return cmd.ExecuteScalar() as string;
    }
}
