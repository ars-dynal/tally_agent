namespace TallyAgent.Core.Data;

/// <summary>One completed (or abandoned) sync run, as the console shows it.</summary>
public sealed record RunRecord(
    string SyncId, string Mode, string StartedUtc, string? FinishedUtc, string Status,
    string? WindowFrom, string? WindowTo,
    int DatasetsAttempted, int DatasetsSucceeded, long RecordsQueued,
    string? DatasetsFailed, string? ErrorMessage)
{
    public TimeSpan? Duration =>
        DateTime.TryParse(StartedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var s) &&
        DateTime.TryParse(FinishedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var f)
            ? f - s : null;

    /// <summary>The datasets that did not load, with why — names, not a count.
    /// Stored as "name: reason" lines.</summary>
    public IReadOnlyList<(string Dataset, string Reason)> Failures()
    {
        if (string.IsNullOrWhiteSpace(DatasetsFailed)) return [];
        return [.. DatasetsFailed
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var i = line.IndexOf(':');
                return i < 0 ? (line, "") : (line[..i].Trim(), line[(i + 1)..].Trim());
            })];
    }
}

/// <summary>
/// Local run history — what ran, over what window, how it went.
///
/// The agent stalled on 4-Sep and failed on 5-Sep, and both times the only way
/// to find out was a person opening a console; worse, a SUCCESSFUL run left no
/// trace at all, so "did last night work?" was unanswerable. `batch_manifest`
/// lives in the cloud and is somebody else's; this is the agent's own record.
/// </summary>
public sealed class RunHistoryRepository(AgentDatabase db)
{
    public IReadOnlyList<RunRecord> Recent(int limit = 20)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sync_id, mode, started_utc, finished_utc, status,
                   window_from, window_to, datasets_attempted, datasets_succeeded,
                   records_queued, datasets_failed, error_message
            FROM sync_runs ORDER BY started_utc DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<RunRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RunRecord(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? 0 : r.GetInt32(7),
                r.IsDBNull(8) ? 0 : r.GetInt32(8),
                r.IsDBNull(9) ? 0 : r.GetInt64(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11)));
        return list;
    }

    public RunRecord? Latest() => Recent(1).FirstOrDefault();

    /// <summary>Per-dataset delivery: how much this agent queued, how much the
    /// cloud has acknowledged, and when it last did. Answers the question a
    /// reviewer actually asks — "how do I know the data got there?"</summary>
    public IReadOnlyList<(string Dataset, long Queued, long Acked, string? LastAckUtc, long Pending, long Failed)>
        DeliveryByDataset()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        // batch_history holds acked batches; upload_batches holds everything
        // still in flight or stuck.
        cmd.CommandText = """
            SELECT dataset,
                   SUM(acked_records)   AS acked,
                   MAX(acked_at)        AS last_ack,
                   SUM(pending_records) AS pending,
                   SUM(failed_records)  AS failed
            FROM (
              SELECT dataset, record_count AS acked_records, completed_utc AS acked_at,
                     0 AS pending_records, 0 AS failed_records
              FROM batch_history WHERE status='acked'
              UNION ALL
              SELECT dataset, 0, NULL,
                     CASE WHEN status IN ('pending','uploading') THEN record_count ELSE 0 END,
                     CASE WHEN status='failed' THEN record_count ELSE 0 END
              FROM upload_batches
            )
            GROUP BY dataset ORDER BY dataset
            """;
        var list = new List<(string, long, long, string?, long, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var acked = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            var pending = r.IsDBNull(3) ? 0 : r.GetInt64(3);
            var failed = r.IsDBNull(4) ? 0 : r.GetInt64(4);
            list.Add((r.GetString(0), acked + pending + failed, acked,
                      r.IsDBNull(2) ? null : r.GetString(2), pending, failed));
        }
        return list;
    }

    /// <summary>Keep 30 days. Run history is for answering "did last night
    /// work?", not for being an archive.</summary>
    public int PruneOlderThan(int days = 30)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sync_runs WHERE started_utc < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-days).ToString("O"));
        return cmd.ExecuteNonQuery();
    }
}
