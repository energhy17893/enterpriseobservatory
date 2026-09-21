using EnterpriseObservatory.Application.Analysis;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Time-to-full answers with a date and its window, or says why it will not.
/// </summary>
/// <remarks>
/// Units are GB and days throughout. Every series is daily from
/// <see cref="T0"/>, and "now" is the last reading unless a test says otherwise.
/// </remarks>
public class TimeToFullTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static TrendPoint[] Daily(int days, Func<int, double> value) =>
        Enumerable.Range(0, days).Select(d => new TrendPoint(T0.AddDays(d), value(d))).ToArray();

    private static DateTimeOffset LastOf(TrendPoint[] points) => points[^1].AtUtc;

    private static TimeToFullResult.Refusal Refused(TimeToFullResult result, TimeToFullRefusalReason reason)
    {
        var refusal = Assert.IsType<TimeToFullResult.Refusal>(result);
        Assert.Equal(reason, refusal.Reason);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Detail));
        return refusal;
    }

    [Fact]
    public void A_steady_linear_fill_gets_the_right_date_and_its_window()
    {
        // 1000 GB rising 20 a day for 30 days; on day 29 it is at 1580, and
        // 420 GB of room at 20 a day is 21 days.
        var points = Daily(30, d => 1000 + 20 * d);
        var now = LastOf(points);

        var forecast = Assert.IsType<TimeToFullResult.Forecast>(TimeToFull.Estimate(points, 2000, now));

        Assert.Equal(20d, forecast.SlopePerDay, precision: 9);
        Assert.Equal(21d, forecast.Days, tolerance: 0.01);
        Assert.Equal(now.AddDays(21), forecast.FullAtUtc, TimeSpan.FromMinutes(15));
        Assert.Equal(new TrendWindow(T0, now), forecast.Window);
        Assert.Equal(TimeSpan.FromDays(29), forecast.Window.Span);
        Assert.Equal(30, forecast.PointsUsed);
        Assert.True(forecast.PValue < 0.05);
    }

    [Fact]
    public void The_date_is_counted_from_now_along_the_line_not_from_the_last_reading()
    {
        // Evaluated five days after the last reading: the line has moved on
        // 100 GB since, so 16 days remain, not 21.
        var points = Daily(30, d => 1000 + 20 * d);

        var forecast = Assert.IsType<TimeToFullResult.Forecast>(
            TimeToFull.Estimate(points, 2000, LastOf(points).AddDays(5)));

        Assert.Equal(16d, forecast.Days, tolerance: 0.01);
    }

    [Fact]
    public void Noise_and_a_few_outliers_leave_theil_sen_close_where_least_squares_is_pulled()
    {
        // 20 GB a day with ±5 GB of noise, and three readings 400 GB high near
        // the end — a backup landing and being cleaned up, say.
        var random = new Random(7);
        var points = Daily(30, d => 1000 + 20 * d + (random.NextDouble() * 10 - 5)
            + (d is 22 or 25 or 28 ? 400 : 0));
        var now = LastOf(points);

        var forecast = Assert.IsType<TimeToFullResult.Forecast>(TimeToFull.Estimate(points, 2000, now));

        Assert.Equal(20d, forecast.SlopePerDay, tolerance: 1d);
        Assert.Equal(21d, forecast.Days, tolerance: 1.5);

        // The same points through least squares: more than a fifth too steep.
        var ols = LeastSquares.Slope(points);
        Assert.True(ols > 24, $"least squares slope {ols}");
        Assert.True(Math.Abs(ols - 20) > 3 * Math.Abs(forecast.SlopePerDay - 20));
    }

    [Fact]
    public void A_flat_series_never_fills()
    {
        var points = Daily(30, _ => 1500);

        var refusal = Refused(TimeToFull.Estimate(points, 2000, LastOf(points)), TimeToFullRefusalReason.NotFilling);

        Assert.Equal(0d, refusal.SlopePerDay);
    }

    [Fact]
    public void A_shrinking_series_never_fills()
    {
        var points = Daily(30, d => 1500 - 10 * d);

        var refusal = Refused(TimeToFull.Estimate(points, 2000, LastOf(points)), TimeToFullRefusalReason.NotFilling);

        Assert.Equal(-10d, refusal.SlopePerDay!.Value, precision: 9);
    }

    [Fact]
    public void Random_noise_has_no_significant_trend()
    {
        var random = new Random(42);
        var points = Daily(60, _ => 500 + random.NextDouble() * 100 - 50);

        var refusal = Refused(
            TimeToFull.Estimate(points, 2000, LastOf(points)), TimeToFullRefusalReason.NoSignificantTrend);

        Assert.NotNull(refusal.SlopePerDay);
        Assert.Equal(new TrendWindow(T0, LastOf(points)), refusal.Window);
    }

    [Fact]
    public void One_big_jump_is_a_step_not_a_trend()
    {
        // A flat datastore with a 1000 GB disk attached on day 15. Mann–Kendall
        // calls that significant — it is perfectly monotonic across the jump —
        // which is exactly why the step has to be caught.
        var random = new Random(3);
        var points = Daily(30, d => 1000 + (d >= 15 ? 1000 : 0) + random.NextDouble() * 2 - 1);

        Refused(TimeToFull.Estimate(points, 5000, LastOf(points)), TimeToFullRefusalReason.StepChange);
    }

    [Fact]
    public void A_jump_on_top_of_slow_growth_is_still_a_step()
    {
        var points = Daily(30, d => 1000 + 2 * d + (d >= 10 ? 1000 : 0));

        Refused(TimeToFull.Estimate(points, 5000, LastOf(points)), TimeToFullRefusalReason.StepChange);
    }

    [Fact]
    public void A_single_spike_is_not_a_step()
    {
        var points = Daily(30, d => 1000 + 20 * d + (d == 15 ? 2000 : 0));

        Assert.IsType<TimeToFullResult.Forecast>(TimeToFull.Estimate(points, 3000, LastOf(points)));
    }

    [Fact]
    public void Fewer_points_than_the_minimum_are_refused()
    {
        var points = Daily(13, d => 1000 + 20 * d);

        var refusal = Refused(TimeToFull.Estimate(points, 2000, LastOf(points)), TimeToFullRefusalReason.TooFewPoints);

        Assert.Equal(13, refusal.PointsUsed);
        Assert.NotNull(refusal.Window);
    }

    [Fact]
    public void No_points_at_all_are_too_few_and_have_no_window()
    {
        var refusal = Refused(TimeToFull.Estimate([], 2000, T0), TimeToFullRefusalReason.TooFewPoints);

        Assert.Null(refusal.Window);
        Assert.Equal(0, refusal.PointsUsed);
    }

    [Fact]
    public void Enough_points_over_too_short_a_window_are_refused()
    {
        var points = Enumerable.Range(0, 48).Select(h => new TrendPoint(T0.AddHours(h), 1000 + h)).ToArray();

        Refused(TimeToFull.Estimate(points, 2000, LastOf(points)), TimeToFullRefusalReason.WindowTooShort);
    }

    [Fact]
    public void A_full_datastore_is_already_full()
    {
        var points = Daily(30, d => 1500 + 20 * d);

        Refused(TimeToFull.Estimate(points, 2000, LastOf(points)), TimeToFullRefusalReason.AlreadyFull);
    }

    [Fact]
    public void A_fill_date_past_the_horizon_is_not_given()
    {
        // 1 GB a day with 1000 GB of room: about three years, not a date.
        var points = Daily(30, d => 1000 + d);

        var refusal = Refused(TimeToFull.Estimate(points, 2029, LastOf(points)), TimeToFullRefusalReason.BeyondHorizon);

        Assert.Equal(1d, refusal.SlopePerDay!.Value, precision: 9);
    }

    [Fact]
    public void The_usage_floor_is_off_by_default_and_refuses_when_set()
    {
        // 16% used, 20 GB a day into 10 000 GB: 421 days, which the default
        // horizon would refuse, so widen it to see the floor alone.
        var points = Daily(30, d => 1000 + 20 * d);
        var wide = new TimeToFullPolicy { Horizon = TimeSpan.FromDays(1000) };

        Assert.IsType<TimeToFullResult.Forecast>(TimeToFull.Estimate(points, 10_000, LastOf(points), wide));

        var floored = wide with { UsageFloorFraction = 0.6 };
        Refused(TimeToFull.Estimate(points, 10_000, LastOf(points), floored), TimeToFullRefusalReason.BelowUsageFloor);
    }

    [Fact]
    public void The_minimum_point_count_is_policy()
    {
        var points = Daily(10, d => 1000 + 20 * d);
        var policy = new TimeToFullPolicy { MinimumPoints = 8 };

        Assert.IsType<TimeToFullResult.Forecast>(TimeToFull.Estimate(points, 2000, LastOf(points), policy));
    }

    [Fact]
    public void Points_arrive_in_any_order()
    {
        var points = Daily(30, d => 1000 + 20 * d);
        var shuffled = points.OrderBy(p => p.Value % 7).ThenByDescending(p => p.AtUtc).ToArray();

        var forecast = Assert.IsType<TimeToFullResult.Forecast>(
            TimeToFull.Estimate(shuffled, 2000, LastOf(points)));

        Assert.Equal(new TrendWindow(T0, LastOf(points)), forecast.Window);
    }

    [Fact]
    public void A_non_positive_capacity_is_a_caller_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeToFull.Estimate(Daily(30, d => d), 0, T0));
    }
}
