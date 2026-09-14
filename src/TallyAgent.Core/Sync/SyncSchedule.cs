namespace TallyAgent.Core.Sync;

/// <summary>
/// When the next scheduled sync is due. Two shapes: every N minutes from the end
/// of the last cycle, or once a day at a fixed LOCAL time. Pure, so it can be
/// tested without a clock.
/// </summary>
public static class SyncSchedule
{
    /// <summary>Parses "HH:mm" (24-hour). Null when blank or malformed.</summary>
    public static TimeOnly? ParseDailyAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TimeOnly.TryParseExact(value.Trim(), "HH:mm", null,
            System.Globalization.DateTimeStyles.None, out var t) ? t : null;
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

    /// <summary>Next run in UTC, from either shape of schedule.</summary>
    public static DateTime NextRunUtc(DateTime nowUtc, string? dailyAt, int intervalMinutes)
    {
        var at = ParseDailyAt(dailyAt);
        if (at is null) return nowUtc + TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
        var local = NextDailyRun(nowUtc.ToLocalTime(), at.Value);
        return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
    }

    public static string Describe(string? dailyAt, int intervalMinutes) =>
        ParseDailyAt(dailyAt) is { } t ? $"daily at {t.ToString("HH:mm")} (local time)" : $"every {intervalMinutes} min";
}
