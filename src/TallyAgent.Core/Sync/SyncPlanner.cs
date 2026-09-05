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
    int RecoveredGapDays,
    /// <summary>For a full sync: the oldest date the walk must reach. The
    /// engine marks the full sync done when a completed window's From reaches
    /// this date. Null for incremental plans.</summary>
    DateOnly? TargetStart = null);

/// <summary>
/// Pure voucher-window planning (extracted from SyncEngine for testability).
///
/// Full sync is NEWEST-FIRST: chunked windows walk from today BACKWARDS to the
/// extraction start, so the most recent (most valuable) data lands in BigQuery
/// first and an interrupted history walk resumes from where it stopped going
/// further back. The checkpoint frontier is LastFromDate (the oldest date
/// extracted so far); LastToDate records the newest date covered.
///
/// Incremental: the window ALWAYS starts at the earlier of
///   (checkpoint.LastToDate + 1)  and  (today − lookbackDays)
/// so an outage longer than the lookback can never create a silent gap —
/// the missed days are re-extracted and the gap is surfaced to the caller
/// for alerting. Long gaps are chunked (oldest-first, as they are small).
/// </summary>
public static class SyncPlanner
{
    /// <summary>Sentinel written to SyncCheckpoint.LastAlterId marking a
    /// checkpoint produced by the newest-first (v2) full-sync walk. Older
    /// forward-walk checkpoints (null/other) cannot be resumed backwards and
    /// are ignored, restarting the walk (batch dedup makes that cheap).</summary>
    public const long NewestFirstCheckpointMarker = 2;

    /// <summary>What clamping a requested range to Tally's books decided.</summary>
    public enum BooksClamp
    {
        /// <summary>Wholly inside the books; use the range as asked.</summary>
        Ok,
        /// <summary>The tail overshot the books end and was trimmed.</summary>
        Trimmed,
        /// <summary>Starts before the books begin — nothing there to serve.</summary>
        BeforeBooksStart,
        /// <summary>Starts after the books end — clamping would leave nothing.</summary>
        AfterBooksEnd,
    }

    /// <summary>
    /// Trim a requested range to the end of Tally's books.
    ///
    /// The incremental window always ends TODAY; Tally's books always end at the
    /// last voucher anyone entered. So on any morning before the first voucher
    /// of the day is posted, the request overshoots the books by a day or two.
    /// That is ordinary, not an error, and the days below the overshoot are real
    /// data that must still load.
    ///
    /// Pure so it can be tested directly: the version of this that lived inside
    /// SyncEngine rejected a whole valid week and nothing caught it.
    /// </summary>
    public static (BooksClamp Outcome, DateOnly To) ClampToBooks(
        DateOnly from, DateOnly to, DateOnly booksFrom, DateOnly booksTo)
    {
        if (from < booksFrom) return (BooksClamp.BeforeBooksStart, to);
        if (from > booksTo) return (BooksClamp.AfterBooksEnd, to);
        if (to <= booksTo) return (BooksClamp.Ok, to);
        return (BooksClamp.Trimmed, booksTo);
    }

    /// <summary>
    /// True when <c>extractionStartDate</c> currently has NO effect.
    ///
    /// It is only ever read inside the <c>!FullSyncDone</c> branch below, so once
    /// the full-history walk has completed and the checkpoint has latched, the
    /// setting is inert: editing it changes nothing at all until the checkpoint
    /// is reset (Force Full Sync / <c>ResetVoucherCheckpoint</c>). A silently
    /// ignored setting is a trap, so both the Manager and the service say so
    /// out loud rather than letting someone believe they have changed the range.
    /// </summary>
    public static bool ExtractionStartDateIsInert(SyncCheckpoint? voucherCheckpoint) =>
        voucherCheckpoint is { FullSyncDone: true };

    public static VoucherPlan PlanVoucherWindows(
        TallySettings settings, SyncCheckpoint? checkpoint, DateOnly today)
    {
        var windows = new List<(DateOnly, DateOnly)>();
        var chunk = Math.Max(1, settings.FullSyncChunkDays);

        if (checkpoint is not { FullSyncDone: true })
        {
            var target = ResolveExtractionStart(settings, today);
            // Upper bound of the walk. extractionEndDate confines a backfill run
            // to a single financial year, so no window is ever asked to cover
            // more than that year.
            var ceiling = ResolveExtractionEnd(settings, today);
            if (ceiling < target) ceiling = target;

            // Resume a newest-first walk: continue backwards from the day
            // before the oldest window already completed.
            var top = ceiling;
            if (checkpoint?.LastAlterId == NewestFirstCheckpointMarker &&
                TryParseIsoDate(checkpoint.LastFromDate) is { } frontier)
            {
                if (frontier <= target) // walk already reached the start
                    return new VoucherPlan(windows, IsFullSync: true, 0, target);
                top = frontier.AddDays(-1);
                if (top > ceiling) top = ceiling;
            }

            AddChunkedNewestFirst(windows, target, top, chunk);
            return new VoucherPlan(windows, IsFullSync: true, 0, target);
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
        return new VoucherPlan(windows, IsFullSync: false, gapDays);
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

    /// <summary>Chunked windows ordered newest-first: the first window ends at
    /// <paramref name="end"/> and each subsequent window is the chunk before,
    /// down to <paramref name="start"/>. Windows stay internally ascending
    /// (From ≤ To) so extraction and checkpoints are unchanged.</summary>
    private static void AddChunkedNewestFirst(List<(DateOnly, DateOnly)> windows,
        DateOnly start, DateOnly end, int chunkDays)
    {
        for (var to = end; to >= start; to = to.AddDays(-chunkDays))
        {
            var from = to.AddDays(-(chunkDays - 1));
            if (from < start) from = start;
            windows.Add((from, to));
        }
    }

    /// <summary>Upper bound of the historical walk. When extractionEndDate is
    /// set the backfill stops there instead of at today, so one run can be
    /// confined to a single financial year. Blank, unparseable, or a date at or
    /// after today all mean "walk to today".</summary>
    private static DateOnly ResolveExtractionEnd(TallySettings settings, DateOnly today)
    {
        if (TryParseIsoDate(settings.ExtractionEndDate) is { } configured && configured < today)
            return configured;
        return today;
    }

    private static DateOnly ResolveExtractionStart(TallySettings settings, DateOnly today)
    {
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
