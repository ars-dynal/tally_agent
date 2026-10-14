using TallyAgent.Core.Sync;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>2.4.6 month-end snapshots: which month-ends are asked for, and when.</summary>
public class MonthEndPlannerTests
{
    private static readonly DateOnly Fy = new(2026, 4, 1);

    [Fact]
    public void First_night_backfills_every_closed_month_newest_first()
    {
        var due = MonthEndPlanner.Due(new DateOnly(2026, 10, 15), Fy, 20, 8, _ => null);
        Assert.Equal(new[]
        {
            new DateOnly(2026, 9, 30), new DateOnly(2026, 8, 31), new DateOnly(2026, 7, 31),
            new DateOnly(2026, 6, 30), new DateOnly(2026, 5, 31), new DateOnly(2026, 4, 30),
        }, due);
    }

    [Fact]
    public void Backfill_is_spread_over_nights_by_max_per_night()
    {
        var due = MonthEndPlanner.Due(new DateOnly(2026, 10, 15), Fy, 20, 2, _ => null);
        Assert.Equal(new[] { new DateOnly(2026, 9, 30), new DateOnly(2026, 8, 31) }, due);
    }

    [Fact]
    public void Last_month_is_refreshed_nightly_until_the_20th()
    {
        var sep = new DateOnly(2026, 9, 30);
        DateOnly? Captured(DateOnly m) => m == sep ? new DateOnly(2026, 10, 14) : new DateOnly(2026, 10, 14);
        Assert.Contains(sep, MonthEndPlanner.Due(new DateOnly(2026, 10, 15), Fy, 20, 8, Captured));
        Assert.DoesNotContain(new DateOnly(2026, 8, 31),
            MonthEndPlanner.Due(new DateOnly(2026, 10, 15), Fy, 20, 8, Captured));
    }

    [Fact]
    public void Not_asked_twice_on_the_same_day()
    {
        var today = new DateOnly(2026, 10, 15);
        Assert.Empty(MonthEndPlanner.Due(today, Fy, 20, 8, _ => today));
    }

    [Fact]
    public void One_final_capture_after_the_freeze_date_then_frozen()
    {
        var sep = new DateOnly(2026, 9, 30);
        // last captured on the 19th, today is the 22nd: one more, to include entries up to the 20th
        Assert.Equal(new[] { sep }, MonthEndPlanner.Due(new DateOnly(2026, 10, 22), Fy, 20, 8,
            m => m == sep ? new DateOnly(2026, 10, 19) : new DateOnly(2026, 10, 22)));
        // captured on the 22nd: frozen
        Assert.Empty(MonthEndPlanner.Due(new DateOnly(2026, 10, 25), Fy, 20, 8,
            _ => new DateOnly(2026, 10, 22)));
    }

    [Fact]
    public void January_reaches_back_into_the_previous_calendar_year()
    {
        var due = MonthEndPlanner.Due(new DateOnly(2027, 1, 5), new DateOnly(2026, 12, 1), 20, 8, _ => null);
        Assert.Equal(new[] { new DateOnly(2026, 12, 31) }, due);
    }

    [Theory]
    [InlineData(21, true)]
    [InlineData(23, true)]
    [InlineData(2, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(12, false)]
    [InlineData(20, false)]
    public void Only_outside_office_hours(int hour, bool allowed) =>
        Assert.Equal(allowed, MonthEndPlanner.InWindow(hour, 21, 7));
}
