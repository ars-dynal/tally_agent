using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Sync;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>SQLite-backed tests for sequence monotonicity, duplicate enqueue,
/// and batch-ID stability across simulated application restarts.</summary>
public class BatchQueueTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public BatchQueueTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tally-agent-tests-" + Guid.NewGuid().ToString("N"));
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

    private static QueuedBatch MakeBatch(string batchId, long seq, string payloadPath) => new(
        BatchId: batchId, Dataset: "vouchers", Company: "Test Co", SequenceNo: seq,
        SyncId: "sync1", ExtractStartUtc: "2026-07-30T00:00:00Z",
        ExtractEndUtc: "2026-07-30T00:01:00Z", WindowFrom: "2026-07-23", WindowTo: "2026-07-30",
        RecordCount: 10, PayloadPath: payloadPath, PayloadBytes: 100,
        ChecksumSha256: new string('a', 64), SchemaVersion: "1.0",
        Status: "pending", RetryCount: 0, NextAttemptUtc: null, LastError: null,
        CreatedUtc: "2026-07-30T00:01:00Z");

    [Fact]
    public void SequenceNumbers_NeverRegress_AfterAck()
    {
        var db = NewDb();
        var repo = new BatchQueueRepository(db);

        var s1 = repo.NextSequence("vouchers");
        var payload = Path.Combine(_dir, "b1.ndjson.gz");
        File.WriteAllBytes(payload, [1, 2, 3]);
        Assert.True(repo.TryEnqueue(MakeBatch("batch-1", s1, payload)));

        // Ack moves the row to batch_history and deletes it from upload_batches.
        repo.Ack("batch-1");
        Assert.Empty(repo.ListByStatus("pending"));

        // Sequence must still advance past the acked batch, not reset to s1.
        var s2 = repo.NextSequence("vouchers");
        Assert.True(s2 > s1, $"sequence regressed: first={s1}, after-ack={s2}");
    }

    [Fact]
    public void DuplicateEnqueue_IsSilentNoOp()
    {
        var db = NewDb();
        var repo = new BatchQueueRepository(db);
        var payload = Path.Combine(_dir, "b2.ndjson.gz");
        File.WriteAllBytes(payload, [1, 2, 3]);

        var batch = MakeBatch("dup-batch", 1, payload);
        Assert.True(repo.TryEnqueue(batch));
        Assert.False(repo.TryEnqueue(batch));           // second attempt: no throw, no insert

        Assert.Single(repo.ListByStatus("pending"));    // exactly one row
        Assert.True(File.Exists(payload));              // payload untouched
    }

    [Fact]
    public void Restart_DoesNotGenerateNewBatchId_ForExistingQueuedBatch()
    {
        var config = TestConfig();
        var queueDir = Path.Combine(_dir, "queue");

        string originalId;
        {
            var db = NewDb();
            var builder = new BatchBuilder(new BatchQueueRepository(db), config, queueDir);
            var rows = new List<Dictionary<string, object?>>
            {
                new() { ["guid"] = "v-001", ["amount"] = 100.0 },
                new() { ["guid"] = "v-002", ["amount"] = -100.0 },
            };
            var ids = builder.BuildAndEnqueue("vouchers", "Test Co", "sync1", rows,
                DateTime.UtcNow, DateTime.UtcNow, "2026-07-23", "2026-07-30", 5000);
            originalId = Assert.Single(ids);
        }

        // Simulate an application restart: fresh database + repository instances.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        {
            var db = NewDb();
            var repo = new BatchQueueRepository(db);
            var pending = Assert.Single(repo.ListByStatus("pending"));

            Assert.Equal(originalId, pending.BatchId);                       // stored, not regenerated
            Assert.Equal(originalId + ".ndjson.gz",
                Path.GetFileName(pending.PayloadPath));                      // file matches id
            Assert.True(File.Exists(pending.PayloadPath));

            var dequeued = repo.DequeueNextDue(DateTime.UtcNow);
            Assert.NotNull(dequeued);
            Assert.Equal(originalId, dequeued!.BatchId);                     // upload path reuses id
        }
    }

    [Fact]
    public void Builder_ProducesDeterministicFormat_AndPersistsChecksum()
    {
        var db = NewDb();
        var repo = new BatchQueueRepository(db);
        var builder = new BatchBuilder(repo, TestConfig(), Path.Combine(_dir, "queue"));

        var rows = new List<Dictionary<string, object?>> { new() { ["guid"] = "v-1" } };
        var ids = builder.BuildAndEnqueue("ledgers", "Test Co", "sync1", rows,
            DateTime.UtcNow, DateTime.UtcNow, null, null, 5000);

        var id = Assert.Single(ids);
        var row = Assert.Single(repo.ListByStatus("pending"));

        Assert.StartsWith("TEST-AGENT-test-co-ledgers-na-na-", id);          // §9.2 format, no timestamp
        Assert.EndsWith(row.ContentChecksum[..12], id);                      // id embeds CONTENT checksum
        Assert.Equal(64, row.ContentChecksum.Length);
        Assert.Equal(64, row.ChecksumSha256.Length);                         // transport checksum kept
        Assert.NotEqual(row.ContentChecksum, row.ChecksumSha256);
    }

    [Fact]
    public void CrashReplay_SameExtraction_ReusesId_AndNeverDeletesOriginalPayload()
    {
        // NOTE: no timestamp pinning — the replay runs with a DIFFERENT sync id and
        // timestamp, proving the content checksum (audit fields excluded) drives
        // identity exactly as it will in production.
        var config = TestConfig();
        var queueDir = Path.Combine(_dir, "queue");
        var db = NewDb();
        var repo = new BatchQueueRepository(db);
        var builder = new BatchBuilder(repo, config, queueDir);
        var rows1 = new List<Dictionary<string, object?>> { new() { ["guid"] = "v-1" } };

        var id1 = Assert.Single(builder.BuildAndEnqueue("vouchers", "Test Co", "sync1", rows1,
            DateTime.UtcNow, DateTime.UtcNow, "2026-07-23", "2026-07-30", 5000));
        var payload = Assert.Single(repo.ListByStatus("pending")).PayloadPath;
        var originalBytes = File.ReadAllBytes(payload);
        var originalWriteTime = File.GetLastWriteTimeUtc(payload);

        // Simulate crash-after-rename-before-insert: remove the row, keep the file.
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM upload_batches WHERE batch_id=$id";
            cmd.Parameters.AddWithValue("$id", id1);
            cmd.ExecuteNonQuery();
        }

        // Replay the identical extraction with a NEW sync id (fresh cycle after crash).
        var rows2 = new List<Dictionary<string, object?>> { new() { ["guid"] = "v-1" } };
        var id2 = Assert.Single(builder.BuildAndEnqueue("vouchers", "Test Co", "sync2", rows2,
            DateTime.UtcNow, DateTime.UtcNow, "2026-07-23", "2026-07-30", 5000));

        Assert.Equal(id1, id2);                                              // same deterministic id
                                                                             // despite new sync id/ts
        Assert.Equal(originalBytes, File.ReadAllBytes(payload));             // original untouched
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(payload));  // not rewritten
        Assert.Empty(Directory.GetFiles(queueDir, "*.tmp"));                 // redundant tmp removed
        Assert.Single(repo.ListByStatus("pending"));                         // row restored, once
    }

    [Fact]
    public void DuplicateBuild_WithRowPresent_IsNoOp_AndLeavesNoTmpFiles()
    {
        var config = TestConfig();
        var queueDir = Path.Combine(_dir, "queue");
        var db = NewDb();
        var repo = new BatchQueueRepository(db);
        var builder = new BatchBuilder(repo, config, queueDir);
        var rows = new List<Dictionary<string, object?>> { new() { ["guid"] = "v-1" } };

        var first = builder.BuildAndEnqueue("vouchers", "Test Co", "sync1", rows,
            DateTime.UtcNow, DateTime.UtcNow, "2026-07-23", "2026-07-30", 5000);
        Assert.Single(first);

        // Identical build (new sync id) while the original row is still pending:
        // the equivalent-active-batch suppression matches on content checksum.
        var second = builder.BuildAndEnqueue("vouchers", "Test Co", "sync2",
            new List<Dictionary<string, object?>> { new() { ["guid"] = "v-1" } },
            DateTime.UtcNow, DateTime.UtcNow, "2026-07-23", "2026-07-30", 5000);

        Assert.Empty(second);                                                // duplicate excluded
        Assert.Single(repo.ListByStatus("pending"));                         // still exactly one row
        Assert.Single(Directory.GetFiles(queueDir, "*.ndjson.gz"));          // one payload
        Assert.Empty(Directory.GetFiles(queueDir, "*.tmp"));                 // tmp cleaned up
    }

    [Fact]
    public void SequenceRemainsMonotonic_AcrossEnqueueAckRestartCycles()
    {
        var config = TestConfig();
        var queueDir = Path.Combine(_dir, "queue");
        long lastSeq = 0;

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            // "Restart": fresh database + repository instances each cycle.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var db = NewDb();
            var repo = new BatchQueueRepository(db);
            var builder = new BatchBuilder(repo, config, queueDir);

            var rows = new List<Dictionary<string, object?>>
                { new() { ["guid"] = $"v-{cycle}" } };
            var id = Assert.Single(builder.BuildAndEnqueue("vouchers", "Test Co",
                $"sync{cycle}", rows, DateTime.UtcNow, DateTime.UtcNow,
                "2026-07-23", "2026-07-30", 5000));

            var row = Assert.Single(repo.ListByStatus("pending"));
            Assert.True(row.SequenceNo > lastSeq,
                $"cycle {cycle}: sequence {row.SequenceNo} did not advance past {lastSeq}");
            lastSeq = row.SequenceNo;

            repo.Ack(id);                                    // drain the queue every cycle
            Assert.Empty(repo.ListByStatus("pending"));
        }
    }

    [Fact]
    public void SchemaMigrations_AddSequenceAndContentChecksum()
    {
        var db = NewDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sequence_no FROM batch_history LIMIT 0";
        cmd.ExecuteReader();                                                 // throws if column missing
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT content_checksum FROM upload_batches LIMIT 0";
        cmd2.ExecuteReader();                                                // v3 column present
        Assert.Equal(5, AgentDatabase.CurrentSchemaVersion);
    }
}

