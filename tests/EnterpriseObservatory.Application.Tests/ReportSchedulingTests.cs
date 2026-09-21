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

    // --- DST: fall-back (clocks go back, a duplicate offset is at stake) ---

    [Fact]
    public void Berlin_daily_before_its_hour_on_fall_back_day_uses_yesterdays_offset_not_todays()
    {
        // Europe/Berlin falls back on 2026-10-25: CEST (+02:00) until 03:00
        // local, then CET (+01:00). At 08:00 local -- already CET -- the
        // schedule's 09:00 slot has not happened yet today, so the due
        // occurrence is yesterday's 09:00, which was still CEST (+02:00),
        // i.e. 07:00 UTC. Reusing today's +01:00 offset for yesterday would
        // compute 08:00 UTC instead and make the dispatcher resend.
        var schedule = DailyAtSeven with { HourLocal = 9, TimeZoneId = "Europe/Berlin" };

        var now = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.FromHours(1));

        var occurrence = ReportScheduling.LastOccurrenceUtc(schedule, now);

        Assert.Equal(new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero), occurrence);
    }

    [Fact]
    public void Berlin_daily_after_its_hour_on_fall_back_day_uses_todays_new_offset()
    {
        // At 10:00 local on the same day, today's 09:00 slot -- CET (+01:00)
        // -- has already happened, so it is the one due: 08:00 UTC.
        var schedule = DailyAtSeven with { HourLocal = 9, TimeZoneId = "Europe/Berlin" };

        var now = new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.FromHours(1));

        var occurrence = ReportScheduling.LastOccurrenceUtc(schedule, now);

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero), occurrence);
    }

    [Fact]
    public void Berlin_weekly_before_its_hour_on_fall_back_day_steps_back_a_full_week_with_the_right_offset()
    {
        // 2026-10-25 is a Sunday; a weekly schedule on Sunday behaves the
        // same as the daily case but steps back seven days instead of one.
        var schedule = DailyAtSeven with
        {
            Frequency = ReportFrequency.Weekly,
            DayOfWeek = DayOfWeek.Sunday,
            HourLocal = 9,
            TimeZoneId = "Europe/Berlin",
        };

        var now = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.FromHours(1));

        var occurrence = ReportScheduling.LastOccurrenceUtc(schedule, now);

        // 2026-10-18 (the previous Sunday) was still CEST, +02:00.
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 7, 0, 0, TimeSpan.Zero), occurrence);
    }

    // --- DST: spring-forward (clocks jump ahead, a gap hour does not exist) ---

    [Fact]
    public void Berlin_daily_before_its_hour_on_spring_forward_day_uses_yesterdays_offset_not_todays()
    {
        // Europe/Berlin springs forward on 2026-03-29: CET (+01:00) until
        // 02:00 local, which becomes 03:00 CEST (+02:00) at once. At 08:00
        // local -- already CEST -- today's 09:00 slot has not happened yet,
        // so the due occurrence is yesterday's 09:00, still CET (+01:00),
        // i.e. 08:00 UTC.
        var schedule = DailyAtSeven with { HourLocal = 9, TimeZoneId = "Europe/Berlin" };

        var now = new DateTimeOffset(2026, 3, 29, 8, 0, 0, TimeSpan.FromHours(2));

        var occurrence = ReportScheduling.LastOccurrenceUtc(schedule, now);

        Assert.Equal(new DateTimeOffset(2026, 3, 28, 8, 0, 0, TimeSpan.Zero), occurrence);
    }

    [Fact]
    public void Berlin_daily_after_its_hour_on_spring_forward_day_uses_todays_new_offset()
    {
        var schedule = DailyAtSeven with { HourLocal = 9, TimeZoneId = "Europe/Berlin" };

        var now = new DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.FromHours(2));

        var occurrence = ReportScheduling.LastOccurrenceUtc(schedule, now);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 7, 0, 0, TimeSpan.Zero), occurrence);
    }

    // --- DST: America/New_York, the mirror case on the other side of the world ---

    [Fact]
    public void New_York_daily_before_its_hour_on_fall_back_day_uses_yesterdays_offset_not_todays()
    {
        // America/New_York falls back on 2026-11-01: EDT (-04:00) until
        // 02:00 local, then EST (-05:00). At 08:00 local -- already EST --
        // today's 09:00 slot has not happened yet, so the due occurrence is
        // yesterday's 09:00, still EDT (-04:00), i.e. 13:00 UTC.
        var schedule = DailyAtSeven with { HourLocal = 9, TimeZoneId = "America/New_York" };

        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.FromHours(-5));

        var occurrence = ReportScheduling.LastOccurrenceUtc(schedule, now);

        Assert.Equal(new DateTimeOffset(2026, 10, 31, 13, 0, 0, TimeSpan.Zero), occurrence);
    }

    [Fact]
    public void New_York_weekly_before_its_hour_on_fall_back_day_steps_back_a_full_week_with_the_right_offset()
    {
        // 2026-11-01 is a Sunday.
        var schedule = DailyAtSeven with
        {
            Frequency = ReportFrequency.Weekly,
            DayOfWeek = DayOfWeek.Sunday,
            HourLocal = 9,
            TimeZoneId = "America/New_York",
        };

        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.FromHours(-5));

        var occurrence = ReportScheduling.LastOccurrenceUtc(schedule, now);

        // 2026-10-25 (the previous Sunday) was still EDT, -04:00.
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 13, 0, 0, TimeSpan.Zero), occurrence);
    }

    [Fact]
    public void New_York_daily_before_its_hour_on_spring_forward_day_uses_yesterdays_offset_not_todays()
    {
        // America/New_York springs forward on 2026-03-08: EST (-05:00) until
        // 02:00 local, which becomes 03:00 EDT (-04:00) at once. At 08:00
        // local -- already EDT -- today's 09:00 slot has not happened yet,
        // so the due occurrence is yesterday's 09:00, still EST (-05:00),
        // i.e. 14:00 UTC.
        var schedule = DailyAtSeven with { HourLocal = 9, TimeZoneId = "America/New_York" };

        var now = new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.FromHours(-4));

        var occurrence = ReportScheduling.LastOccurrenceUtc(schedule, now);

        Assert.Equal(new DateTimeOffset(2026, 3, 7, 14, 0, 0, TimeSpan.Zero), occurrence);
    }
}
