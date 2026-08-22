using TallyAgent.Core.Sync;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>Adaptive-down windowing (v2.0.5): once a window proves too slow,
/// every remaining window is shrunk — not just the one that timed out.</summary>
public sealed class WindowReChunkTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    [Fact]
    public void ReChunk_SplitsWideWindows_KeepsNarrowOnes_ForwardOrder()
    {
        var q = new Queue<(DateOnly From, DateOnly To)>([(D(1, 1), D(1, 10)), (D(1, 11), D(1, 12))]);
        SyncEngine.ReChunk(q, 4, newestFirst: false);
        Assert.Equal([(D(1, 1), D(1, 4)), (D(1, 5), D(1, 8)), (D(1, 9), D(1, 10)), (D(1, 11), D(1, 12))], q.ToList());
    }

    [Fact]
    public void ReChunk_NewestFirst_EmitsNewerPiecesFirst()
    {
        var q = new Queue<(DateOnly From, DateOnly To)>([(D(3, 1), D(3, 9))]);
        SyncEngine.ReChunk(q, 3, newestFirst: true);
        Assert.Equal([(D(3, 7), D(3, 9)), (D(3, 4), D(3, 6)), (D(3, 1), D(3, 3))], q.ToList());
    }

    [Fact]
    public void ReChunk_NeverBelowOneDay_AndCoversEveryDayExactlyOnce()
    {
        var q = new Queue<(DateOnly From, DateOnly To)>([(D(2, 1), D(2, 28))]);
        SyncEngine.ReChunk(q, 0, newestFirst: false);
        Assert.Equal(28, q.Count);
        Assert.All(q, w => Assert.Equal(w.From, w.To));
        var days = q.Select(w => w.From).Distinct().Count();
        Assert.Equal(28, days);
    }
}
