using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Sync;
using Xunit;

namespace TallyAgent.Core.Tests;

using Row = Dictionary<string, object?>;

/// <summary>
/// v2.2.0 — masters are no longer re-uploaded unchanged every cycle.
///
/// The dangerous failure here is not "we uploaded too much", it is "masters
/// silently stopped uploading and nothing noticed". These tests pin the four
/// behaviours that keep that impossible:
///   • a first-ever extraction uploads (nothing confirmed yet);
///   • an extraction whose content changed uploads;
///   • an extraction identical to one the cloud ACKNOWLEDGED is skipped;
///   • an upload that was not acknowledged never records a hash — so a failure
///     can never suppress the next attempt.
/// Plus the one that makes the whole feature real rather than decorative: the
/// audit fields stamped on every upload must not reach the hash.
/// </summary>
public sealed class MasterContentHashTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public MasterContentHashTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "agent.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private AgentDatabase NewDb() => new(NullLogger<AgentDatabase>.Instance, _dbPath);

    private static AgentConfig TestConfig() => new()
    {
        Cloud = new CloudSettings
        {
            AgentId = "TEST-AGENT",
            CompanyId = "test-co",
            IngestionApiUrl = "https://example.invalid",
            Environment = "Testing",
        },
    };

    private static List<Row> Ledgers(params string[] names) =>
        [.. names.Select((n, i) => new Row
        {
            ["master_guid"] = $"guid-{i}",
            ["ledger_name"] = n,
            ["parent_group"] = "Sundry Debtors",
            ["closing_balance"] = 100.0 + i,
        })];

    private const string Dataset = "ledgers";
    private const string Company = "Test Co";

    /// <summary>Extract → enqueue → (optionally) ack, exactly as the engine does.
    /// Returns the hash recorded as pending and the batch ids produced.</summary>
    private (string Hash, List<string> Ids) Enqueue(
        AgentDatabase db, List<Row> rows, int maxRecords = 5000)
    {
        var queue = new BatchQueueRepository(db);
        var builder = new BatchBuilder(queue, TestConfig(), Path.Combine(_dir, "queue"));
        var hashes = new MasterContentHashRepository(db);

        var hash = MasterContentHash.Compute(Dataset, Company, rows);
        var ids = builder.BuildAndEnqueue(Dataset, Company, "sync-" + Guid.NewGuid().ToString("N")[..6],
            rows, DateTime.UtcNow, DateTime.UtcNow, null, null, maxRecords);
        hashes.RecordPending(Dataset, Company, hash, ids);
        return (hash, ids);
    }

    // ── the audit-field trap ─────────────────────────────────────────────

    [Fact]
    public void AuditFields_AreExcluded_SoTheHashSurvivesBeingEnqueued()
    {
        var db = NewDb();
        var rows = Ledgers("Cash", "HDFC");

        var before = MasterContentHash.Compute(Dataset, Company, rows);

        // BuildAndEnqueue STAMPS _sync_id, _sync_timestamp and _company onto the
        // very row objects that were hashed. If those reached the hash, the next
        // cycle could never match and the skip would be a silent no-op that still
        // looked like a working feature.
        new BatchBuilder(new BatchQueueRepository(db), TestConfig(), Path.Combine(_dir, "queue"))
            .BuildAndEnqueue(Dataset, Company, "sync-1", rows,
                DateTime.UtcNow, DateTime.UtcNow, null, null, 5000);

        Assert.Contains("_sync_id", rows[0].Keys);
        Assert.Contains("_sync_timestamp", rows[0].Keys);
        Assert.Contains("_company", rows[0].Keys);
        Assert.Contains("_record_key", rows[0].Keys);
        Assert.Equal(before, MasterContentHash.Compute(Dataset, Company, rows));
    }

    [Fact]
    public void ChangedData_ChangesTheHash()
    {
        var baseline = MasterContentHash.Compute(Dataset, Company, Ledgers("Cash", "HDFC"));

        Assert.NotEqual(baseline, MasterContentHash.Compute(Dataset, Company, Ledgers("Cash", "ICICI")));
        Assert.NotEqual(baseline, MasterContentHash.Compute(Dataset, Company, Ledgers("Cash")));
        Assert.NotEqual(baseline, MasterContentHash.Compute(Dataset, "Other Co", Ledgers("Cash", "HDFC")));
        Assert.NotEqual(baseline, MasterContentHash.Compute("groups", Company, Ledgers("Cash", "HDFC")));

        // A balance moving by one paisa is a real change.
        var moved = Ledgers("Cash", "HDFC");
        moved[0]["closing_balance"] = 100.01;
        Assert.NotEqual(baseline, MasterContentHash.Compute(Dataset, Company, moved));
    }

    [Fact]
    public void KeyOrderWithinARow_IsNotAContentChange()
    {
        var a = new List<Row> { new() { ["b"] = 2, ["a"] = 1 } };
        var b = new List<Row> { new() { ["a"] = 1, ["b"] = 2 } };
        Assert.Equal(MasterContentHash.Compute(Dataset, Company, a),
                     MasterContentHash.Compute(Dataset, Company, b));
    }

    // ── the four upload rules ────────────────────────────────────────────

    [Fact]
    public void FirstRun_HasNoConfirmedHash_SoTheExtractionUploads()
    {
        var db = NewDb();
        var hashes = new MasterContentHashRepository(db);
        Assert.Null(hashes.ConfirmedHash(Dataset, Company));
        Assert.Null(hashes.Get(Dataset, Company));
    }

    [Fact]
    public void UnchangedContent_SkipsOnlyAfterTheUploadIsAcknowledged()
    {
        var db = NewDb();
        var queue = new BatchQueueRepository(db);
        var hashes = new MasterContentHashRepository(db);
        var rows = Ledgers("Cash", "HDFC");

        var (hash, ids) = Enqueue(db, rows);
        Assert.NotEmpty(ids);

        // Enqueued but not yet delivered: the next cycle must still upload.
        Assert.Null(hashes.ConfirmedHash(Dataset, Company));
        Assert.Equal(hash, hashes.Get(Dataset, Company)!.PendingHash);

        foreach (var id in ids) queue.Ack(id);

        // Delivered: an identical extraction may now be skipped.
        Assert.Equal(hash, hashes.ConfirmedHash(Dataset, Company));
        Assert.Equal(hash, MasterContentHash.Compute(Dataset, Company, Ledgers("Cash", "HDFC")));
        Assert.Null(hashes.Get(Dataset, Company)!.PendingHash);
    }

    [Fact]
    public void ChangedContent_DoesNotMatchTheConfirmedHash_SoItUploads()
    {
        var db = NewDb();
        var queue = new BatchQueueRepository(db);
        var hashes = new MasterContentHashRepository(db);

        var (_, ids) = Enqueue(db, Ledgers("Cash", "HDFC"));
        foreach (var id in ids) queue.Ack(id);

        var changed = MasterContentHash.Compute(Dataset, Company, Ledgers("Cash", "ICICI"));
        Assert.NotEqual(changed, hashes.ConfirmedHash(Dataset, Company));
    }

    [Fact]
    public void FailedUpload_DoesNotRecordTheHash_AndRecoversOnRetry()
    {
        var db = NewDb();
        var queue = new BatchQueueRepository(db);
        var hashes = new MasterContentHashRepository(db);

        var (hash, ids) = Enqueue(db, Ledgers("Cash", "HDFC"));
        foreach (var id in ids) queue.MarkFailed(id, "ingestion rejected the batch");

        // THE important assertion: a failed upload must never license a skip,
        // or the dataset stops uploading and nothing ever says so.
        Assert.Null(hashes.ConfirmedHash(Dataset, Company));

        queue.RetryAllFailed();
        foreach (var id in ids) queue.Ack(id);
        Assert.Equal(hash, hashes.ConfirmedHash(Dataset, Company));
    }

    [Fact]
    public void PartiallyAcknowledgedUpload_DoesNotConfirm()
    {
        var db = NewDb();
        var queue = new BatchQueueRepository(db);
        var hashes = new MasterContentHashRepository(db);

        // maxRecords=1 over three ledgers ⇒ three batches, as a real master
        // export splits into several.
        var (hash, ids) = Enqueue(db, Ledgers("Cash", "HDFC", "ICICI"), maxRecords: 1);
        Assert.Equal(3, ids.Count);

        queue.Ack(ids[0]);
        Assert.Null(hashes.ConfirmedHash(Dataset, Company));
        queue.Ack(ids[1]);
        Assert.Null(hashes.ConfirmedHash(Dataset, Company));

        queue.Ack(ids[2]);
        Assert.Equal(hash, hashes.ConfirmedHash(Dataset, Company));
    }

    [Fact]
    public void AckOfAnUnrelatedDataset_DoesNotConfirmThisOne()
    {
        var db = NewDb();
        var queue = new BatchQueueRepository(db);
        var hashes = new MasterContentHashRepository(db);
        var builder = new BatchBuilder(queue, TestConfig(), Path.Combine(_dir, "queue"));

        var (_, _) = Enqueue(db, Ledgers("Cash"));
        var otherIds = builder.BuildAndEnqueue("groups", Company, "sync-2", Ledgers("Assets"),
            DateTime.UtcNow, DateTime.UtcNow, null, null, 5000);
        foreach (var id in otherIds) queue.Ack(id);

        Assert.Null(hashes.ConfirmedHash(Dataset, Company));
    }

    [Fact]
    public void ForceFullSync_ClearsHashes_SoAReWalkReUploadsEverything()
    {
        var db = NewDb();
        var queue = new BatchQueueRepository(db);
        var hashes = new MasterContentHashRepository(db);

        var (_, ids) = Enqueue(db, Ledgers("Cash", "HDFC"));
        foreach (var id in ids) queue.Ack(id);
        Assert.NotNull(hashes.ConfirmedHash(Dataset, Company));

        hashes.Clear(Company);
        Assert.Null(hashes.ConfirmedHash(Dataset, Company));
    }
}
