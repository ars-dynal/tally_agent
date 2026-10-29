namespace TallyAgent.Core.Sync;

/// <summary>
/// When the next scheduled sync is due. Two shapes: every N minutes from the end
/// of the last cycle, or once a day at a fixed LOCAL time. Pure, so it can be
/// tested without a clock.
/// </summary>
public static class SyncSchedule
{
    /// <summary>Parses "HH:mm" (24-hour), or several separated by commas
    /// ("19:00, 21:30", 2.5.1). Returns the earliest. Null when blank or when any
    /// entry is malformed -- a half-understood schedule is not a schedule.</summary>
    public static TimeOnly? ParseDailyAt(string? value)
    {
        var all = ParseDailyTimes(value);
        return all.Count == 0 ? null : all[0];
    }

    /// <summary>Every time in the daily schedule, sorted. Empty when blank or malformed.</summary>
    public static List<TimeOnly> ParseDailyTimes(string? value)
    {
        var list = new List<TimeOnly>();
        if (string.IsNullOrWhiteSpace(value)) return list;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TimeOnly.TryParseExact(part, "HH:mm", null, System.Globalization.DateTimeStyles.None, out var t))
                return [];
            if (!list.Contains(t)) list.Add(t);
        }
        list.Sort();
        return list;
    }

    /// <summary>
    /// The next occurrence of <paramref name="at"/> strictly after <paramref name="nowLocal"/>.
    /// A run that ends at 21:00:30 on a 21:00 schedule is due tomorrow, not now.
    /// </summary>
    public static DateTime NextDailyRun(DateTime nowLocal, TimeOnly at)
    {
        var today = nowLocal.Date + at.ToTimeSpan();
        return today > nowLocal ? today : today.AddDays(1);
    }

    /// <summary>The soonest of several daily slots strictly after <paramref name="nowLocal"/>.</summary>
    public static DateTime NextDailyRun(DateTime nowLocal, IEnumerable<TimeOnly> times) =>
        times.Select(t => NextDailyRun(nowLocal, t)).Min();

    /// <summary>Next run in UTC, from either shape of schedule.</summary>
    public static DateTime NextRunUtc(DateTime nowUtc, string? dailyAt, int intervalMinutes)
    {
        var times = ParseDailyTimes(dailyAt);
        if (times.Count == 0) return nowUtc + TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
        var local = NextDailyRun(nowUtc.ToLocalTime(), times);
        return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
    }

    public static string Describe(string? dailyAt, int intervalMinutes)
    {
        var times = ParseDailyTimes(dailyAt);
        return times.Count > 0
            ? $"daily at {string.Join(", ", times.Select(t => t.ToString("HH:mm")))} (local time)"
            : $"every {intervalMinutes} min";
    }
}
