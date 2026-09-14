using Microsoft.Extensions.Logging.Abstractions;
using TallyAgent.Core.Data;
using TallyAgent.Core.Notifications;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>
/// Run history is what the console reads. Before it existed, a SUCCESSFUL run
/// left no trace, so "did last night work?" could not be answered, and a failed
/// one showed a count without naming which datasets it lost.
/// </summary>
public sealed class RunHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rh-" + Guid.NewGuid().ToString("N"));
    public RunHistoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentDatabase NewDb() =>
        new(NullLogger<AgentDatabase>.Instance, Path.Combine(_dir, "agent.db"));

    private static void InsertRun(AgentDatabase db, string id, string status,
        string started, string? failed, int attempted, int ok)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_runs (sync_id, mode, started_utc, finished_utc, status,
                window_from, window_to, datasets_attempted, datasets_succeeded,
                records_queued, datasets_failed)
            VALUES ($id,'incremental',$s,$f,$st,'2026-08-29','2026-09-04',$a,$ok,1234,$fail)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", started);
        cmd.Parameters.AddWithValue("$f", started);
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$a", attempted);
        cmd.Parameters.AddWithValue("$ok", ok);
        cmd.Parameters.AddWithValue("$fail", (object?)failed ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ASuccessfulRun_LeavesATrace()
    {
        var db = NewDb();
        InsertRun(db, "r1", "success", DateTime.UtcNow.ToString("O"), null, 30, 30);

        var last = new RunHistoryRepository(db).Latest();

        Assert.NotNull(last);
        Assert.Equal("success", last!.Status);
        Assert.Equal("2026-08-29", last.WindowFrom);
        Assert.Equal(30, last.DatasetsSucceeded);
        Assert.Empty(last.Failures());
    }

    [Fact]
    public void AFailedRun_NamesTheDatasets_NotJustACount()
    {
        var db = NewDb();
        InsertRun(db, "r2", "partial", DateTime.UtcNow.ToString("O"),
            "trial_balance: Tally refused the request outright rather than returning data.\n" +
            "bank_book: Tally took too long to answer and the request was abandoned.", 30, 28);

        var failures = new RunHistoryRepository(db).Latest()!.Failures();

        Assert.Equal(2, failures.Count);
        Assert.Equal("trial_balance", failures[0].Dataset);
        Assert.Contains("refused", failures[0].Reason);
        Assert.Equal("bank_book", failures[1].Dataset);
    }

    [Fact]
    public void HistoryIsPrunedTo30Days()
    {
        var db = NewDb();
        InsertRun(db, "old", "success", DateTime.UtcNow.AddDays(-45).ToString("O"), null, 30, 30);
        InsertRun(db, "new", "success", DateTime.UtcNow.ToString("O"), null, 30, 30);

        var repo = new RunHistoryRepository(db);
        Assert.Equal(1, repo.PruneOlderThan(30));
        Assert.Single(repo.Recent(20));
    }

    [Fact]
    public void EveryErrorCategory_HasPlainLanguage_AndAnAction()
    {
        // "TallyActivePeriodTooNarrow" means nothing to an accountant.
        foreach (ErrorCategory c in Enum.GetValues<ErrorCategory>())
        {
            var e = PlainLanguage.Describe(c);
            Assert.False(string.IsNullOrWhiteSpace(e.What), $"{c} has no explanation");
            Assert.False(string.IsNullOrWhiteSpace(e.Action), $"{c} has no suggested action");
            Assert.DoesNotContain(c.ToString(), e.What);   // no jargon echoed back
        }
    }

    [Fact]
    public void TheNarrowPeriodError_TellsYouToPressAltF2()
    {
        var e = PlainLanguage.Describe(ErrorCategory.TallyActivePeriodTooNarrow);
        Assert.Contains("books", e.What);
        Assert.Contains("Alt+F2", e.Action);
    }
}
