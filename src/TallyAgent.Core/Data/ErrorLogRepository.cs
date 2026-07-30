using System.Security.Cryptography;
using System.Text;
using TallyAgent.Core.Notifications;

namespace TallyAgent.Core.Data;

public sealed record ErrorRecord(
    long Id, string TsUtc, string Category, string Severity, string Message,
    string? StackTrace, string? Operation, string? Dataset, string? BatchId,
    int RetryCount, bool Reported, string GroupKey);

public sealed class ErrorLogRepository(AgentDatabase db)
{
    public long Insert(ErrorCategory category, ErrorSeverity severity, string message,
        string? stackTrace = null, string? operation = null, string? dataset = null,
        string? batchId = null, int retryCount = 0)
    {
        var groupKey = ComputeGroupKey(category.ToString(), dataset ?? "", operation ?? "");
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO error_log
              (ts_utc, category, severity, message, stack_trace, operation, dataset, batch_id, retry_count, reported, group_key)
            VALUES ($ts,$cat,$sev,$msg,$st,$op,$ds,$bid,$rc,0,$gk);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$cat", category.ToString());
        cmd.Parameters.AddWithValue("$sev", severity.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$msg", message.Length > 4000 ? message[..4000] : message);
        cmd.Parameters.AddWithValue("$st", (object?)stackTrace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$op", (object?)operation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ds", (object?)dataset ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bid", (object?)batchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rc", retryCount);
        cmd.Parameters.AddWithValue("$gk", groupKey);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void MarkReported(IEnumerable<long> ids)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE error_log SET reported=1 WHERE id IN ({string.Join(',', ids)})";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Unreported non-critical errors grouped for the periodic digest.</summary>
    public IReadOnlyList<(string GroupKey, string Category, string? Dataset, long Count, string LastMessage, List<long> Ids)>
        GetUnreportedGroups()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT group_key, category, dataset, COUNT(*), MAX(message), GROUP_CONCAT(id)
            FROM error_log
            WHERE reported=0 AND severity <> 'critical'
            GROUP BY group_key
            """;
        var list = new List<(string, string, string?, long, string, List<long>)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ids = r.GetString(5).Split(',').Select(long.Parse).ToList();
            list.Add((r.GetString(0), r.GetString(1),
                      r.IsDBNull(2) ? null : r.GetString(2),
                      r.GetInt64(3), r.GetString(4), ids));
        }
        return list;
    }

    public IReadOnlyList<ErrorRecord> Recent(int limit = 50)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM error_log ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<ErrorRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ErrorRecord(
                r.GetInt64(r.GetOrdinal("id")),
                r.GetString(r.GetOrdinal("ts_utc")),
                r.GetString(r.GetOrdinal("category")),
                r.GetString(r.GetOrdinal("severity")),
                r.GetString(r.GetOrdinal("message")),
                r.IsDBNull(r.GetOrdinal("stack_trace")) ? null : r.GetString(r.GetOrdinal("stack_trace")),
                r.IsDBNull(r.GetOrdinal("operation")) ? null : r.GetString(r.GetOrdinal("operation")),
                r.IsDBNull(r.GetOrdinal("dataset")) ? null : r.GetString(r.GetOrdinal("dataset")),
                r.IsDBNull(r.GetOrdinal("batch_id")) ? null : r.GetString(r.GetOrdinal("batch_id")),
                r.GetInt32(r.GetOrdinal("retry_count")),
                r.GetInt64(r.GetOrdinal("reported")) == 1,
                r.IsDBNull(r.GetOrdinal("group_key")) ? "" : r.GetString(r.GetOrdinal("group_key"))));
        return list;
    }

    public string? LastErrorMessage()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT category || ': ' || message FROM error_log ORDER BY id DESC LIMIT 1";
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Delete entries older than the retention window (default 90 days).</summary>
    public int Purge(int retentionDays = 90)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM error_log WHERE ts_utc < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-retentionDays).ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    private static string ComputeGroupKey(params string[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return Convert.ToHexString(bytes)[..16];
    }
}
