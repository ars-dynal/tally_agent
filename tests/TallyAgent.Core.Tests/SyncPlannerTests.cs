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

    private static SyncCheckpoint NewestFirstCp(string lastFrom, string lastTo) =>
        new("_vouchers_window", "Co", lastFrom, lastTo,
            SyncPlanner.NewestFirstCheckpointMarker, null, false);

    // ── clamping a window to Tally's books (v2.4.0) ──────────────────────

    /// <summary>
    /// THE regression, from 2026-09-05: the incremental window ran
    /// 29-Aug..05-Sep while Tally's books ended 04-Sep, because nobody had
    /// posted a voucher yet that morning. The old guard rejected the WHOLE
    /// window, so a week of real data did not load — and the run still reported
    /// "Failed batches: 0".
    /// </summary>
    [Fact]
    public void AWindowOvershootingTheBooksEnd_IsTrimmed_NotRejected()
    {
        var (outcome, to) = SyncPlanner.ClampToBooks(
            from: new DateOnly(2026, 8, 29), to: new DateOnly(2026, 9, 5),
            booksFrom: new DateOnly(2019, 4, 1), booksTo: new DateOnly(2026, 9, 4));

        Assert.Equal(SyncPlanner.BooksClamp.Trimmed, outcome);
        Assert.Equal(new DateOnly(2026, 9, 4), to);   // the valid week still loads
    }

    [Fact]
    public void AWindowInsideTheBooks_IsLeftAlone()
    {
        var (outcome, to) = SyncPlanner.ClampToBooks(
            new DateOnly(2026, 8, 29), new DateOnly(2026, 9, 4),
            new DateOnly(2019, 4, 1), new DateOnly(2026, 9, 4));

        Assert.Equal(SyncPlanner.BooksClamp.Ok, outcome);
        Assert.Equal(new DateOnly(2026, 9, 4), to);
    }

    /// <summary>The guard's real purpose survives: an operator who has narrowed
    /// Alt+F2 so the window starts before the books is still an error, because
    /// that range genuinely cannot be served.</summary>
    [Fact]
    public void AWindowStartingBeforeTheBooks_IsStillAnError()
    {
        var (outcome, _) = SyncPlanner.ClampToBooks(
            new DateOnly(2019, 1, 1), new DateOnly(2019, 6, 30),
            new DateOnly(2019, 4, 1), new DateOnly(2026, 9, 4));

        Assert.Equal(SyncPlanner.BooksClamp.BeforeBooksStart, outcome);
    }

    /// <summary>Clamping must never produce an empty or inverted window.</summary>
    [Fact]
    public void AWindowStartingAfterTheBooksEnd_IsStillAnError()
    {
        var (outcome, _) = SyncPlanner.ClampToBooks(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7),
            new DateOnly(2019, 4, 1), new DateOnly(2026, 9, 4));

        Assert.Equal(SyncPlanner.BooksClamp.AfterBooksEnd, outcome);
    }

    [Fact]
    public void ASingleDayAtTheBooksEnd_Survives()
    {
        // The boundary itself: from == to == booksTo must not be trimmed away.
        var (outcome, to) = SyncPlanner.ClampToBooks(
            new DateOnly(2026, 9, 4), new DateOnly(2026, 9, 4),
            new DateOnly(2019, 4, 1), new DateOnly(2026, 9, 4));

        Assert.Equal(SyncPlanner.BooksClamp.Ok, outcome);
        Assert.Equal(new DateOnly(2026, 9, 4), to);
    }

    // ── extractionStartDate: inert once the checkpoint latches (v2.2.0) ──

    /// <summary>The setting is only read inside the !FullSyncDone branch, so
    /// after a completed history walk it does nothing at all. That is fine as
    /// behaviour and unacceptable as SILENT behaviour — the Manager and the
    /// service both say so, and this is the predicate they share.</summary>
    [Fact]
    public void ExtractionStartDate_IsInert_OnlyAfterTheFullSyncCompletes()
    {
        Assert.False(SyncPlanner.ExtractionStartDateIsInert(null));
        Assert.False(SyncPlanner.ExtractionStartDateIsInert(Cp("2026-07-29", fullDone: false)));
        Assert.True(SyncPlanner.ExtractionStartDateIsInert(Cp("2026-07-29", fullDone: true)));
    }

    [Fact]
    public void OnceLatched_ChangingTheStartDateChangesNothing()
    {
        var latched = Cp("2026-07-29", fullDone: true);
        var asConfigured = SyncPlanner.PlanVoucherWindows(Settings(start: "2026-04-01"), latched, Today);
        var movedBackSixYears = SyncPlanner.PlanVoucherWindows(Settings(start: "2020-04-01"), latched, Today);

        // Identical plans: the only thing that re-reads the start date is a
        // checkpoint reset (Force Full Sync).
        Assert.Equal(asConfigured.Windows, movedBackSixYears.Windows);
        Assert.False(movedBackSixYears.IsFullSync);

        // ...and after a reset it takes effect.
        var afterReset = SyncPlanner.PlanVoucherWindows(Settings(start: "2020-04-01"), null, Today);
        Assert.True(afterReset.IsFullSync);
        Assert.Equal(new DateOnly(2020, 4, 1), afterReset.TargetStart);
    }

    // ── full sync: newest-first ───────────────────────────────────

    [Fact]
    public void FirstRun_PlansNewestFirstFullSync_TodayBackToConfiguredStart()
    {
        var plan = SyncPlanner.PlanVoucherWindows(Settings(), null, Today);
        Assert.True(plan.IsFullSync);
        Assert.Equal(new DateOnly(2026, 4, 1), plan.TargetStart);

        // Newest window first, oldest last.
        Assert.Equal(Today, plan.Windows[0].To);
        Assert.Equal(new DateOnly(2026, 4, 1), plan.Windows[^1].From);

        Assert.All(plan.Windows, w => Assert.True(w.From <= w.To));
        Assert.All(plan.Windows, w => Assert.True(
            w.To.DayNumber - w.From.DayNumber + 1 <= 31, "chunk exceeded 31 days"));

        // Windows are contiguous going backwards with no gaps or overlaps.
        for (var i = 1; i < plan.Windows.Count; i++)
            Assert.Equal(plan.Windows[i - 1].From.AddDays(-1), plan.Windows[i].To);
    }

    [Fact]
    public void InterruptedNewestFirstFullSync_ResumesBackwardsFromFrontier()
    {
        // Walk got down to 2026-06-01 before the interruption: next windows
        // continue at 2026-05-31 going backwards; nothing newer is re-planned.
        var plan = SyncPlanner.PlanVoucherWindows(
            Settings(), NewestFirstCp("2026-06-01", "2026-07-30"), Today);
        Assert.True(plan.IsFullSync);
        Assert.Equal(new DateOnly(2026, 5, 31), plan.Windows[0].To);
        Assert.Equal(new DateOnly(2026, 4, 1), plan.Windows[^1].From);
    }

    [Fact]
    public void NewestFirstFullSync_FrontierAtTarget_PlansNothing()
    {
        var plan = SyncPlanner.PlanVoucherWindows(
            Settings(), NewestFirstCp("2026-04-01", "2026-07-30"), Today);
        Assert.True(plan.IsFullSync);
        Assert.Empty(plan.Windows);
    }

    [Fact]
    public void LegacyForwardCheckpoint_IsIgnored_AndWalkRestartsNewestFirst()
    {
        // Old (pre-v2.0.2) full-sync checkpoints walked forward; they cannot be
        // resumed backwards, so the planner restarts (batch dedup absorbs the
        // re-extraction cost).
        var legacy = Cp("2026-05-31", fullDone: false);
        var plan = SyncPlanner.PlanVoucherWindows(Settings(), legacy, Today);
        Assert.True(plan.IsFullSync);
        Assert.Equal(Today, plan.Windows[0].To);
        Assert.Equal(new DateOnly(2026, 4, 1), plan.Windows[^1].From);
    }

    // ── incremental (unchanged, forward) ──────────────────────────

    [Fact]
    public void SteadyState_Incremental_CoversLookbackWindow()
    {
        // Checkpoint is current (yesterday) — plain lookback window, no gap.
        var plan = SyncPlanner.PlanVoucherWindows(Settings(lookback: 7), Cp("2026-07-29", fullDone: true), Today);
        Assert.False(plan.IsFullSync);
        Assert.Null(plan.TargetStart);
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
        Assert.Equal(new DateOnly(2026, 4, 1), plan.Windows[^1].From);
        var febPlan = SyncPlanner.PlanVoucherWindows(Settings(start: ""), null, new DateOnly(2026, 2, 10));
        Assert.Equal(new DateOnly(2025, 4, 1), febPlan.Windows[^1].From); // FY rolls back before April
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

    // -- bounded backfill: extractionEndDate confines the walk ------

    [Fact]
    public void ExtractionEndDate_BoundsFullSyncToThatFinancialYear()
    {
        var settings = new TallySettings
        {
            FullSyncChunkDays = 31,
            ExtractionStartDate = "2019-04-01",
            ExtractionEndDate = "2020-03-31",
        };

        var plan = SyncPlanner.PlanVoucherWindows(settings, null, Today);

        Assert.True(plan.IsFullSync);
        Assert.Equal(new DateOnly(2019, 4, 1), plan.TargetStart);

        // Newest window ends at the configured end date, not at today.
        Assert.Equal(new DateOnly(2020, 3, 31), plan.Windows[0].To);
        Assert.Equal(new DateOnly(2019, 4, 1), plan.Windows[^1].From);

        // Nothing outside the financial year is ever requested.
        Assert.All(plan.Windows, w => Assert.True(w.From >= new DateOnly(2019, 4, 1)));
        Assert.All(plan.Windows, w => Assert.True(w.To <= new DateOnly(2020, 3, 31)));
    }

    [Fact]
    public void ExtractionEndDate_Blank_StillWalksToToday()
    {
        var settings = Settings();
        settings.ExtractionEndDate = "";

        var plan = SyncPlanner.PlanVoucherWindows(settings, null, Today);

        Assert.Equal(Today, plan.Windows[0].To);
    }

    [Fact]
    public void ExtractionEndDate_InTheFuture_IsIgnored()
    {
        var settings = Settings();
        settings.ExtractionEndDate = "2099-01-01";

        var plan = SyncPlanner.PlanVoucherWindows(settings, null, Today);

        Assert.Equal(Today, plan.Windows[0].To);
    }

    [Fact]
    public void ExtractionEndDate_ResumedWalk_NeverExceedsTheCeiling()
    {
        var settings = new TallySettings
        {
            FullSyncChunkDays = 31,
            ExtractionStartDate = "2019-04-01",
            ExtractionEndDate = "2020-03-31",
        };

        // Frontier sits above the ceiling (left over from an unbounded run).
        var plan = SyncPlanner.PlanVoucherWindows(
            settings, NewestFirstCp("2025-01-01", "2026-07-30"), Today);

        Assert.NotEmpty(plan.Windows);
        Assert.All(plan.Windows, w => Assert.True(w.To <= new DateOnly(2020, 3, 31)));
    }

}
