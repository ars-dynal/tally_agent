using System.Globalization;
using TallyAgent.Core.Configuration;
using TallyAgent.Core.Data;

namespace TallyAgent.Core.Sync;

public sealed record VoucherPlan(
    List<(DateOnly From, DateOnly To)> Windows,
    bool IsFullSync,
    /// <summary>Days between the checkpointed high-water mark and the lookback
    /// horizon that would have been silently skipped by lookback-only planning.
    /// &gt; 0 means the agent recovered an outage gap this cycle.</summary>
    int RecoveredGapDays);

/// <summary>
/// Pure voucher-window planning (extracted from SyncEngine for testability).
///
/// Full sync: chunked [start → today] windows, resuming from the checkpoint.
/// Incremental: the window ALWAYS starts at the earlier of
///   (checkpoint.LastToDate + 1)  and  (today − lookbackDays)
/// so an outage longer than the lookback can never create a silent gap —
/// the missed days are re-extracted and the gap is surfaced to the caller
/// for alerting. Long gaps are chunked like a full sync.
/// </summary>
public static class SyncPlanner
{
    public static VoucherPlan PlanVoucherWindows(
        TallySettings settings, SyncCheckpoint? checkpoint, DateOnly today)
    {
        var windows = new List<(DateOnly, DateOnly)>();
        var chunk = Math.Max(1, settings.FullSyncChunkDays);

        if (checkpoint is not { FullSyncDone: true })
        {
            var start = ResolveExtractionStart(settings, checkpoint, today);
            AddChunked(windows, start, today, chunk);
            return new VoucherPlan(windows, IsFullSync: true, RecoveredGapDays: 0);
        }

        var lookback = Math.Max(0, settings.IncrementalLookbackDays);
        var lookbackStart = today.AddDays(-lookback);

        // Resume point: the day after the last successfully extracted window.
        var resumeStart = TryParseIsoDate(checkpoint.LastToDate) is { } lastTo
            ? lastTo.AddDays(1)
            : lookbackStart;
        if (resumeStart > today) resumeStart = today;

        var start2 = resumeStart < lookbackStart ? resumeStart : lookbackStart;
        var gapDays = resumeStart < lookbackStart
            ? lookbackStart.DayNumber - resumeStart.DayNumber
            : 0;

        AddChunked(windows, start2, today, chunk);
        return new VoucherPlan(windows, IsFullSync: false, RecoveredGapDays: gapDays);
    }

    private static void AddChunked(List<(DateOnly, DateOnly)> windows,
        DateOnly start, DateOnly end, int chunkDays)
    {
        for (var from = start; from <= end; from = from.AddDays(chunkDays))
        {
            var to = from.AddDays(chunkDays - 1);
            if (to > end) to = end;
            windows.Add((from, to));
        }
    }

    private static DateOnly ResolveExtractionStart(
        TallySettings settings, SyncCheckpoint? checkpoint, DateOnly today)
    {
        // Resume after crash: continue from day after last completed window
        if (TryParseIsoDate(checkpoint?.LastToDate) is { } lastTo)
            return lastTo.AddDays(1);
        if (TryParseIsoDate(settings.ExtractionStartDate) is { } configured)
            return configured;
        // Default: start of current financial year (April 1, India)
        var fyYear = today.Month >= 4 ? today.Year : today.Year - 1;
        return new DateOnly(fyYear, 4, 1);
    }

    /// <summary>Culture-invariant exact ISO parse (checkpoints are always yyyy-MM-dd).</summary>
    public static DateOnly? TryParseIsoDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
}
