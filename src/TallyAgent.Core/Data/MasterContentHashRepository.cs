using Microsoft.Data.Sqlite;

namespace TallyAgent.Core.Data;

/// <summary>State of the master re-upload skip for one (dataset, company).</summary>
public sealed record MasterContentHashState(
    string? ConfirmedHash, string? ConfirmedUtc, string? PendingHash, int PendingBatches);

/// <summary>
/// Remembers the content hash of the last master extraction the cloud actually
/// ACKNOWLEDGED, so an identical extraction next cycle is not uploaded again.
///
/// Masters are re-extracted every cycle and were re-uploaded every cycle even
/// when byte-identical: ~10,757 rows hourly, ~247,000 redundant rows across the
/// back-fill. <see cref="BatchQueueRepository.FindEquivalentActiveBatch"/>
/// already suppresses a duplicate while the earlier batch is still queued, but
/// an acked batch leaves upload_batches, so the next cycle mints a fresh one.
/// This table is the missing durable memory.
///
/// THE ORDERING IS THE SAFETY PROPERTY. A hash is recorded as PENDING at enqueue
/// and only promoted to CONFIRMED when every batch it produced has been acked.
/// Only a confirmed hash licenses a skip. Recording at enqueue time would mean a
/// batch that later fails permanently suppresses all future uploads of that
/// dataset — masters silently not uploading, which nothing notices.
/// </summary>
public sealed class MasterContentHashRepository(AgentDatabase db)
{
    /// <summary>The hash whose upload was acknowledged, or null. Only this value
    /// may be compared against a fresh extraction to skip an upload.</summary>
    public string? ConfirmedHash(string dataset, string company)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT confirmed_hash FROM master_content_hashes WHERE dataset=$d AND company=$c";
        cmd.Parameters.AddWithValue("$d", dataset);
        cmd.Parameters.AddWithValue("$c", company);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Record a freshly enqueued extraction as pending confirmation.
    /// <paramref name="batchIds"/> must be the batches actually enqueued; an
    /// empty list records nothing, because nothing will ever ack to confirm it.</summary>
    public void RecordPending(string dataset, string company, string hash,
        IReadOnlyList<string> batchIds)
    {
        if (batchIds.Count == 0) return;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO master_content_hashes
              (dataset, company, confirmed_hash, confirmed_utc, pending_hash, pending_batches, pending_utc)
            VALUES ($d,$c,NULL,NULL,$h,$b,$ts)
            ON CONFLICT(dataset, company) DO UPDATE SET
              pending_hash=$h, pending_batches=$b, pending_utc=$ts
            """;
        cmd.Parameters.AddWithValue("$d", dataset);
        cmd.Parameters.AddWithValue("$c", company);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$b", Join(batchIds));
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public MasterContentHashState? Get(string dataset, string company)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT confirmed_hash, confirmed_utc, pending_hash, pending_batches
            FROM master_content_hashes WHERE dataset=$d AND company=$c
            """;
        cmd.Parameters.AddWithValue("$d", dataset);
        cmd.Parameters.AddWithValue("$c", company);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new MasterContentHashState(
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? 0 : Split(r.GetString(3)).Count);
    }

    /// <summary>Forget everything known about a dataset — used by Force Full
    /// Sync so a re-walk always re-uploads.</summary>
    public void Clear(string company)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM master_content_hashes WHERE company=$c";
        cmd.Parameters.AddWithValue("$c", company);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Called from <see cref="BatchQueueRepository.Ack"/> INSIDE the ack
    /// transaction: drop <paramref name="batchId"/> from the pending set and, if
    /// it was the last one outstanding, promote the pending hash to confirmed.
    /// Same transaction as the ack, so a crash can never leave a hash confirmed
    /// for an upload that was not.
    /// </summary>
    internal static void ConfirmBatchWithin(SqliteConnection conn, SqliteTransaction tx,
        string dataset, string company, string batchId)
    {
        string? pendingHash;
        List<string> remaining;
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT pending_hash, pending_batches FROM master_content_hashes
                WHERE dataset=$d AND company=$c
                """;
            sel.Parameters.AddWithValue("$d", dataset);
            sel.Parameters.AddWithValue("$c", company);
            using var r = sel.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0)) return;          // nothing pending here
            pendingHash = r.GetString(0);
            remaining = r.IsDBNull(1) ? [] : Split(r.GetString(1));
        }

        if (!remaining.Remove(batchId)) return;              // not one of ours

        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        if (remaining.Count > 0)
        {
            upd.CommandText = """
                UPDATE master_content_hashes SET pending_batches=$b
                WHERE dataset=$d AND company=$c
                """;
            upd.Parameters.AddWithValue("$b", Join(remaining));
        }
        else
        {
            // Last batch of this extraction acknowledged — the whole dataset is
            // now known to be in the warehouse, so this hash may suppress the
            // next identical upload.
            upd.CommandText = """
                UPDATE master_content_hashes
                SET confirmed_hash=$h, confirmed_utc=$ts,
                    pending_hash=NULL, pending_batches=NULL, pending_utc=NULL
                WHERE dataset=$d AND company=$c
                """;
            upd.Parameters.AddWithValue("$h", pendingHash);
            upd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        }
        upd.Parameters.AddWithValue("$d", dataset);
        upd.Parameters.AddWithValue("$c", company);
        upd.ExecuteNonQuery();
    }

    private static string Join(IReadOnlyList<string> ids) => string.Join(' ', ids);

    private static List<string> Split(string value) =>
        [.. value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
