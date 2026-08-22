using System.Text.Json;

namespace TallyAgent.Core.Data;

/// <summary>Last known computed balances per master record (keyed by Tally
/// GUID). Written once per day when the agent asks Tally for balances on the
/// snapshot slot; read on every other master export so the ledgers /
/// stock_items datasets always carry balance columns without forcing Tally to
/// re-value the company on each cycle.</summary>
public sealed class MasterBalanceRepository(AgentDatabase db)
{
    public void Save(string dataset, string company,
        IReadOnlyDictionary<string, Dictionary<string, double>> byGuid)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO master_balances (dataset, company, guid, values_json, captured_utc)
            VALUES ($d,$c,$g,$v,$ts)
            ON CONFLICT(dataset, company, guid) DO UPDATE SET values_json=$v, captured_utc=$ts
            """;
        var pd = cmd.Parameters.Add("$d", Microsoft.Data.Sqlite.SqliteType.Text);
        var pc = cmd.Parameters.Add("$c", Microsoft.Data.Sqlite.SqliteType.Text);
        var pg = cmd.Parameters.Add("$g", Microsoft.Data.Sqlite.SqliteType.Text);
        var pv = cmd.Parameters.Add("$v", Microsoft.Data.Sqlite.SqliteType.Text);
        var pts = cmd.Parameters.Add("$ts", Microsoft.Data.Sqlite.SqliteType.Text);
        var now = DateTime.UtcNow.ToString("O");
        foreach (var (guid, values) in byGuid)
        {
            if (guid.Length == 0) continue;
            pd.Value = dataset; pc.Value = company; pg.Value = guid;
            pv.Value = JsonSerializer.Serialize(values); pts.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public Dictionary<string, Dictionary<string, double>> Load(string dataset, string company)
    {
        var result = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT guid, values_json FROM master_balances WHERE dataset=$d AND company=$c";
        cmd.Parameters.AddWithValue("$d", dataset);
        cmd.Parameters.AddWithValue("$c", company);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(1));
            if (values is not null) result[r.GetString(0)] = values;
        }
        return result;
    }

    /// <summary>UTC timestamp of the most recent capture for the dataset, or null.</summary>
    public string? LastCapturedUtc(string dataset, string company)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(captured_utc) FROM master_balances WHERE dataset=$d AND company=$c";
        cmd.Parameters.AddWithValue("$d", dataset);
        cmd.Parameters.AddWithValue("$c", company);
        return cmd.ExecuteScalar() as string;
    }
}
