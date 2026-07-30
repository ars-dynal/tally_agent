namespace TallyAgent.Core.Data;

/// <summary>Buffers heartbeat payloads while offline so history is complete
/// once connectivity returns.</summary>
public sealed class HeartbeatRepository(AgentDatabase db)
{
    public long Insert(string payloadJson, bool delivered)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO heartbeat_history (ts_utc, delivered, payload_json)
            VALUES ($ts,$d,$p);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$d", delivered ? 1 : 0);
        cmd.Parameters.AddWithValue("$p", payloadJson);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void MarkDelivered(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE heartbeat_history SET delivered=1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public int Purge(int retentionDays = 14)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM heartbeat_history WHERE ts_utc < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-retentionDays).ToString("O"));
        return cmd.ExecuteNonQuery();
    }
}
