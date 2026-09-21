using EnterpriseObservatory.Application.Reporting;

namespace EnterpriseObservatory.Application.Tests;

public class ReportSchedulingTests
{
    private static readonly ReportSchedule DailyAtSeven = new()
    {
        Frequency = ReportFrequency.Daily,
        HourLocal = 7,
        TimeZoneId = "UTC",
    };

    [Fact]
    public void A_daily_schedule_is_due_the_first_time_it_is_asked()
    {
        var now = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

        Assert.True(ReportScheduling.IsDue(DailyAtSeven, lastClaimedUtc: null, now));
    }

    [Fact]
    public void A_daily_schedule_is_not_due_again_the_same_day_once_claimed()
    {
        var claimed = new DateTimeOffset(2026, 9, 21, 7, 1, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 9, 21, 23, 0, 0, TimeSpan.Zero);

        Assert.False(ReportScheduling.IsDue(DailyAtSeven, claimed, now));
    }

    [Fact]
    public void A_daily_schedule_is_due_again_the_next_day()
    {
        var claimed = new DateTimeOffset(2026, 9, 21, 7, 1, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 9, 22, 7, 5, 0, TimeSpan.Zero);

        Assert.True(ReportScheduling.IsDue(DailyAtSeven, claimed, now));
    }

    [Fact]
    public void A_daily_schedule_is_not_due_before_its_hour_today()
    {
        var claimed = new DateTimeOffset(2026, 9, 20, 7, 1, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 9, 21, 6, 59, 0, TimeSpan.Zero);

        Assert.False(ReportScheduling.IsDue(DailyAtSeven, claimed, now));
    }

    [Fact]
    public void A_weekly_schedule_fires_only_on_its_day()
    {
        var schedule = DailyAtSeven with { Frequency = ReportFrequency.Weekly, DayOfWeek = DayOfWeek.Monday };

        // 2026-09-21 is a Monday.
        var monday = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var tuesday = new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);

        Assert.True(ReportScheduling.IsDue(schedule, null, monday));

        var claimedMonday = ReportScheduling.LastOccurrenceUtc(schedule, monday);

        Assert.False(ReportScheduling.IsDue(schedule, claimedMonday, tuesday));
    }

    [Fact]
    public void A_service_restart_within_the_same_window_does_not_owe_a_second_send()
    {
        // The idempotency claim: claiming the window and asking again a moment
        // later, as a restart would, must not find a second due occurrence.
        var occurrence = ReportScheduling.LastOccurrenceUtc(
            DailyAtSeven, new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));

        Assert.False(ReportScheduling.IsDue(
            DailyAtSeven, occurrence, occurrence.AddMinutes(5)));
    }

    [Fact]
    public void An_unresolvable_time_zone_is_reported_rather_than_thrown()
    {
        var schedule = DailyAtSeven with { TimeZoneId = "Not/A/Real/Zone" };

        var problems = schedule.Validate();

        Assert.Contains(problems, p => p.Contains("Not/A/Real/Zone", StringComparison.Ordinal));
    }
}
