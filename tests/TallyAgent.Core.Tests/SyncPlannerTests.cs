using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;
using TallyAgent.Core.Sync;
using Xunit;

namespace TallyAgent.Core.Tests;

public class SyncPlannerTests
{
    private static readonly DateOnly Today = new(2026, 7, 30);

    private static TallySettings Settings(int lookback = 7, int chunk = 31, string start = "2026-04-01") =>
        new() { IncrementalLookbackDays = lookback, FullSyncChunkDays = chunk, ExtractionStartDate = start };

    private static SyncCheckpoint Cp(string? lastTo, bool fullDone) =>
        new("_vouchers_window", "Co", null, lastTo, null, null, fullDone);

    [Fact]
    public void FirstRun_PlansChunkedFullSync_FromConfiguredStart()
    {
        var plan = SyncPlanner.PlanVoucherWindows(Settings(), null, Today);
        Assert.True(plan.IsFullSync);
        Assert.Equal(new DateOnly(2026, 4, 1), plan.Windows[0].From);
        Assert.Equal(Today, plan.Windows[^1].To);
        Assert.All(plan.Windows, w => Assert.True(
            w.To.DayNumber - w.From.DayNumber + 1 <= 31, "chunk exceeded 31 days"));
        // windows are contiguous
        for (var i = 1; i < plan.Windows.Count; i++)
            Assert.Equal(plan.Windows[i - 1].To.AddDays(1), plan.Windows[i].From);
    }

    [Fact]
    public void InterruptedFullSync_ResumesFromDayAfterCheckpoint()
    {
        var plan = SyncPlanner.PlanVoucherWindows(Settings(), Cp("2026-05-31", fullDone: false), Today);
        Assert.True(plan.IsFullSync);
        Assert.Equal(new DateOnly(2026, 6, 1), plan.Windows[0].From);
    }

    [Fact]
    public void SteadyState_Incremental_CoversLookbackWindow()
    {
        // Checkpoint is current (yesterday) — plain lookback window, no gap.
        var plan = SyncPlanner.PlanVoucherWindows(Settings(lookback: 7), Cp("2026-07-29", fullDone: true), Today);
        Assert.False(plan.IsFullSync);
        Assert.Equal(0, plan.RecoveredGapDays);
        Assert.Equal(Today.AddDays(-7), plan.Windows[0].From);
        Assert.Equal(Today, plan.Windows[^1].To);
    }

    [Fact]
    public void OutageLongerThanLookback_RecoversGap_InsteadOfSkippingDays()
    {
        // Agent was down 12 days with a 7-day lookback: the OLD planner started at
        // today-7 and silently lost days -12..-8. The new planner must start the
        // day after the checkpoint and report the recovered gap.
        var plan = SyncPlanner.PlanVoucherWindows(Settings(lookback: 7), Cp("2026-07-18", fullDone: true), Today);
        Assert.False(plan.IsFullSync);
        Assert.Equal(new DateOnly(2026, 7, 19), plan.Windows[0].From);   // no gap
        Assert.Equal(Today, plan.Windows[^1].To);
        Assert.Equal(4, plan.RecoveredGapDays);                          // 07-19..07-22 were at risk
    }

    [Fact]
    public void VeryLongOutage_IsChunked()
    {
        var plan = SyncPlanner.PlanVoucherWindows(Settings(lookback: 7, chunk: 31),
            Cp("2026-03-01", fullDone: true), Today);
        Assert.True(plan.Windows.Count > 1);                             // chunked, not one giant window
        Assert.Equal(new DateOnly(2026, 3, 2), plan.Windows[0].From);
        Assert.Equal(Today, plan.Windows[^1].To);
    }

    [Fact]
    public void CheckpointInFuture_IsClamped()
    {
        var plan = SyncPlanner.PlanVoucherWindows(Settings(lookback: 7), Cp("2026-08-15", fullDone: true), Today);
        Assert.Equal(Today.AddDays(-7), plan.Windows[0].From);           // lookback still honoured
        Assert.Equal(Today, plan.Windows[^1].To);
    }

    [Fact]
    public void NoConfiguredStart_DefaultsToFinancialYearStart()
    {
        var plan = SyncPlanner.PlanVoucherWindows(Settings(start: ""), null, Today);
        Assert.Equal(new DateOnly(2026, 4, 1), plan.Windows[0].From);
        var febPlan = SyncPlanner.PlanVoucherWindows(Settings(start: ""), null, new DateOnly(2026, 2, 10));
        Assert.Equal(new DateOnly(2025, 4, 1), febPlan.Windows[0].From); // FY rolls back before April
    }

    [Fact]
    public void IsoDateParsing_IsCultureInvariantAndExact()
    {
        Assert.NotNull(SyncPlanner.TryParseIsoDate("2026-07-30"));
        Assert.Null(SyncPlanner.TryParseIsoDate("30-07-2026"));
        Assert.Null(SyncPlanner.TryParseIsoDate("07/30/2026"));
        Assert.Null(SyncPlanner.TryParseIsoDate(null));
        Assert.Null(SyncPlanner.TryParseIsoDate(""));
    }
}
