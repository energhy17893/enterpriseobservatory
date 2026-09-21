using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// Where a fold restarts after late data, without a database.
/// </summary>
/// <remarks>
/// The rule is wrong in two directions and both are silent: too shallow and a
/// late sample never reaches the long tiers, too deep and a complete bucket is
/// rebuilt from a source retention has already thinned. Pinned here so it holds
/// on a machine with no PostgreSQL.
/// </remarks>
public class RefoldWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly SeriesRetentionPolicy Policy = new();

    [Fact]
    public void Without_a_marker_a_pass_starts_at_the_watermark()
    {
        Assert.Equal(T0, RefoldWindow.Start(T0, null, T0.AddDays(-2)));
    }

    [Fact]
    public void A_marker_below_the_watermark_moves_the_start_back_to_it()
    {
        Assert.Equal(T0.AddMinutes(-20), RefoldWindow.Start(T0, T0.AddMinutes(-20), T0.AddDays(-2)));
    }

    [Fact]
    public void A_marker_never_reaches_below_the_floor()
    {
        var floor = T0.AddDays(-2);

        Assert.Equal(floor, RefoldWindow.Start(T0, T0.AddDays(-5), floor));
    }

    [Fact]
    public void A_marker_at_or_above_the_watermark_changes_nothing()
    {
        Assert.Equal(T0, RefoldWindow.Start(T0, T0.AddMinutes(5), T0.AddDays(-2)));
        Assert.Equal(T0, RefoldWindow.Start(T0, T0, T0.AddDays(-2)));
    }

    [Fact]
    public void The_floor_is_the_first_bucket_starting_inside_source_retention()
    {
        // Raw is kept two days. At 12:03 the cutoff is 12:03 two days ago; the
        // bucket starting 12:00 has already lost three minutes of samples, so
        // the first safe one starts at 12:05.
        var now = T0.AddMinutes(3);

        Assert.Equal(
            T0.AddDays(-2).AddMinutes(5),
            RefoldWindow.Floor(now, Policy, SeriesResolution.FiveMinutes));
    }

    [Fact]
    public void A_floor_on_a_bucket_boundary_is_that_bucket()
    {
        Assert.Equal(T0.AddDays(-2), RefoldWindow.Floor(T0, Policy, SeriesResolution.FiveMinutes));
    }

    [Fact]
    public void The_hourly_floor_follows_five_minute_retention_not_raw()
    {
        // The hour is rebuilt from five-minute buckets, kept thirty days.
        var now = T0.AddMinutes(10);

        Assert.Equal(
            T0.AddDays(-30).AddHours(1),
            RefoldWindow.Floor(now, Policy, SeriesResolution.OneHour));
    }

    [Fact]
    public void Raw_has_no_floor_because_it_is_never_folded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RefoldWindow.Floor(T0, Policy, SeriesResolution.Raw));
    }

    [Fact]
    public void A_late_sample_marks_the_start_of_its_bucket()
    {
        var marked = RefoldWindow.DirtyAfter(
            T0, null, T0.AddMinutes(-17), SeriesResolution.FiveMinutes);

        Assert.Equal(T0.AddMinutes(-20), marked);
    }

    [Fact]
    public void A_sample_at_or_above_the_watermark_marks_nothing()
    {
        // The next pass reaches it anyway, so the steady state writes nothing.
        Assert.Null(RefoldWindow.DirtyAfter(T0, null, T0, SeriesResolution.FiveMinutes));
        Assert.Null(RefoldWindow.DirtyAfter(T0, null, T0.AddMinutes(1), SeriesResolution.FiveMinutes));
    }

    [Fact]
    public void An_earlier_marker_is_kept()
    {
        Assert.Null(RefoldWindow.DirtyAfter(
            T0, T0.AddMinutes(-30), T0.AddMinutes(-10), SeriesResolution.FiveMinutes));
    }

    [Fact]
    public void A_later_marker_is_moved_back()
    {
        Assert.Equal(
            T0.AddMinutes(-30),
            RefoldWindow.DirtyAfter(
                T0, T0.AddMinutes(-10), T0.AddMinutes(-27), SeriesResolution.FiveMinutes));
    }

    [Fact]
    public void A_slice_is_at_most_the_configured_number_of_buckets()
    {
        Assert.Equal(
            T0.AddHours(1),
            RefoldWindow.SliceEnd(T0, T0.AddDays(2), SeriesResolution.FiveMinutes, 12));
        Assert.Equal(
            T0.AddHours(12),
            RefoldWindow.SliceEnd(T0, T0.AddDays(2), SeriesResolution.OneHour, 12));
    }

    [Fact]
    public void A_slice_stops_at_the_last_complete_bucket()
    {
        Assert.Equal(
            T0.AddMinutes(10),
            RefoldWindow.SliceEnd(T0, T0.AddMinutes(10), SeriesResolution.FiveMinutes, 12));
    }

    [Fact]
    public void A_two_day_range_takes_forty_eight_hour_long_slices()
    {
        var start = T0;
        var end = T0.AddDays(2);
        var slices = 0;

        while (start < end)
        {
            var next = RefoldWindow.SliceEnd(start, end, SeriesResolution.FiveMinutes, 12);
            Assert.True(next - start <= TimeSpan.FromHours(1));
            start = next;
            slices++;
        }

        Assert.Equal(48, slices);
    }

    [Fact]
    public void A_slice_below_the_watermark_leaves_the_marker_where_it_stopped()
    {
        Assert.Equal(
            (T0, (DateTimeOffset?)T0.AddHours(-3)),
            RefoldWindow.After(T0, T0.AddHours(-3)));
    }

    [Fact]
    public void A_slice_reaching_the_watermark_clears_the_marker()
    {
        Assert.Equal((T0, (DateTimeOffset?)null), RefoldWindow.After(T0, T0));
    }

    [Fact]
    public void A_slice_past_the_watermark_moves_it()
    {
        Assert.Equal(
            (T0.AddHours(1), (DateTimeOffset?)null),
            RefoldWindow.After(T0, T0.AddHours(1)));
        Assert.Equal(
            (T0.AddHours(1), (DateTimeOffset?)null),
            RefoldWindow.After(null, T0.AddHours(1)));
    }

    [Fact]
    public void The_hourly_marker_is_on_the_hourly_grid()
    {
        // A five-minute rebuild from 11:55 dirties the hour starting 11:00.
        Assert.Equal(
            T0.AddHours(-1),
            RefoldWindow.DirtyAfter(T0, null, T0.AddMinutes(-5), SeriesResolution.OneHour));
    }
}
