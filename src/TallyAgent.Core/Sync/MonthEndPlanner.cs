namespace TallyAgent.Core.Sync;

/// <summary>
/// WHICH MONTH-ENDS TO ASK TALLY FOR TONIGHT (2.4.6). Pure, so it is tested
/// without Tally.
///
/// A month-end M is DUE when:
///   * it has never been captured; or
///   * today is on or before the freeze date (the refresh-until day of the
///     month after M) and it was last captured before today - late entries
///     are still arriving, so it is refreshed nightly; or
///   * the freeze date has passed but the last capture was taken BEFORE it -
///     one final capture, so the frozen copy includes everything posted up to
///     the freeze date.
/// Otherwise it is FROZEN and never asked again.
///
/// Newest first: last month-end matters most (the bank statement), and a
/// back-fill of older months is spread over nights by maxPerNight.
/// </summary>
public static class MonthEndPlanner
{
    public static DateOnly MonthEnd(int year, int month) =>
        new DateOnly(year, month, DateTime.DaysInMonth(year, month));

    public static DateOnly FinancialYearStart(DateOnly d) =>
        new(d.Month >= 4 ? d.Year : d.Year - 1, 4, 1);

    public static DateOnly FreezeDate(DateOnly monthEnd, int refreshUntilDay)
    {
        var next = monthEnd.AddDays(1);
        var day = Math.Clamp(refreshUntilDay, 1, DateTime.DaysInMonth(next.Year, next.Month));
        return new DateOnly(next.Year, next.Month, day);
    }

    public static List<DateOnly> Due(DateOnly today, DateOnly backfillFrom, int refreshUntilDay,
        int maxPerNight, Func<DateOnly, DateOnly?> lastCapturedOn, int recheckDays = 0)
    {
        // WEEKLY RE-CHECK OF FROZEN MONTH-ENDS (2.5.2). A month-end used to be
        // frozen after the 20th of the next month and never asked again, so an
        // entry Accounts changed later in a closed month left the cloud's copy
        // behind Tally: Sep 2026 closing stock was 606.30 under Tally on 31 Oct
        // (17,99,51,731.19 vs 17,99,52,337.49). Frozen month-ends of the current
        // financial year are now asked again once every recheckDays (0 = never).
        var fyStart = FinancialYearStart(today.AddDays(-today.Day));
        var due = new List<DateOnly>();
        var first = MonthEnd(backfillFrom.Year, backfillFrom.Month);
        var lastClosed = today.AddDays(-today.Day); // the last day of the previous month
        for (var m = lastClosed; m >= first; m = m.AddDays(-m.Day))
        {
            var last = lastCapturedOn(m);
            var freeze = FreezeDate(m, refreshUntilDay);
            var isDue = last is null
                || (today <= freeze && last.Value < today)
                || (today > freeze && last.Value < freeze)
                || (recheckDays > 0 && today > freeze && m >= fyStart
                    && last.Value.AddDays(recheckDays) <= today);
            if (isDue) due.Add(m);
            if (due.Count >= Math.Max(1, maxPerNight)) break;
        }
        return due;
    }

    /// <summary>May the step START at this local time? Only outside office hours.</summary>
    public static bool InWindow(int hour, int earliestHour, int latestHourExclusive) =>
        hour >= earliestHour || hour < latestHourExclusive;
}
