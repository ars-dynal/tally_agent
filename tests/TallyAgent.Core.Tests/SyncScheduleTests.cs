using TallyAgent.Core.Sync;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>Once a day at a fixed local time: the schedule the office asked for
/// (read the books after the working day, not every hour during it).</summary>
public class SyncScheduleTests
{
    [Theory]
    [InlineData("21:00", 21, 0)]
    [InlineData("06:30", 6, 30)]
    public void Parses_HH_mm(string text, int h, int m)
    {
        var t = SyncSchedule.ParseDailyAt(text);
        Assert.NotNull(t);
        Assert.Equal(new TimeOnly(h, m), t!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("9pm")]
    [InlineData("25:00")]
    [InlineData(null)]
    public void Blank_or_malformed_means_interval_schedule(string? text)
        => Assert.Null(SyncSchedule.ParseDailyAt(text));

    [Fact]
    public void Before_the_slot_runs_today_after_it_runs_tomorrow()
    {
        var at = new TimeOnly(21, 0);
        var morning = new DateTime(2026, 9, 14, 11, 0, 0);
        Assert.Equal(new DateTime(2026, 9, 14, 21, 0, 0), SyncSchedule.NextDailyRun(morning, at));

        // a cycle that ENDS just after the slot must not fire again until tomorrow
        var justAfter = new DateTime(2026, 9, 14, 21, 0, 30);
        Assert.Equal(new DateTime(2026, 9, 15, 21, 0, 0), SyncSchedule.NextDailyRun(justAfter, at));

        var exactly = new DateTime(2026, 9, 14, 21, 0, 0);
        Assert.Equal(new DateTime(2026, 9, 15, 21, 0, 0), SyncSchedule.NextDailyRun(exactly, at));
    }

    [Fact]
    public void Interval_schedule_is_unchanged_when_daily_is_blank()
    {
        var now = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddMinutes(15), SyncSchedule.NextRunUtc(now, "", 15));
        Assert.Equal("every 15 min", SyncSchedule.Describe("", 15));
        Assert.Equal("daily at 21:00 (local time)", SyncSchedule.Describe("21:00", 15));
    }

    // 2.5.1: several slots a day -- 19:00 for the day's data, 21:30 for the night work.
    [Fact]
    public void Several_daily_slots_run_at_the_soonest_next_one()
    {
        var times = SyncSchedule.ParseDailyTimes("21:30, 19:00");
        Assert.Equal([new TimeOnly(19, 0), new TimeOnly(21, 30)], times);
        Assert.Equal(new TimeOnly(19, 0), SyncSchedule.ParseDailyAt("21:30, 19:00"));
        Assert.Equal(new DateTime(2026, 10, 29, 19, 0, 0), SyncSchedule.NextDailyRun(new DateTime(2026, 10, 29, 11, 0, 0), times));
        Assert.Equal(new DateTime(2026, 10, 29, 21, 30, 0), SyncSchedule.NextDailyRun(new DateTime(2026, 10, 29, 19, 5, 0), times));
        Assert.Equal(new DateTime(2026, 10, 30, 19, 0, 0), SyncSchedule.NextDailyRun(new DateTime(2026, 10, 29, 22, 0, 0), times));
    }

    [Theory]
    [InlineData("19:00, 9pm")]
    [InlineData("19:00,,25:00")]
    public void One_bad_slot_rejects_the_whole_schedule(string text)
        => Assert.Null(SyncSchedule.ParseDailyAt(text));
}
