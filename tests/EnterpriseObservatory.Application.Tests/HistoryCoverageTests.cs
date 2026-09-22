using EnterpriseObservatory.Application.Analysis;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// "Enough history" (ADR-0026, planner decision): the span reaches seven days
/// and at least 80% of the points those seven days should hold were read.
/// One helper, shared by N+1 and datastore time-to-full.
/// </summary>
public class HistoryCoverageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static IEnumerable<DateTimeOffset> Hourly(double days, DateTimeOffset? end = null) =>
        Enumerable.Range(0, (int)(days * 24) + 1).Select(i => (end ?? T0).AddHours(-i));

    [Fact]
    public void Seven_complete_days_are_enough()
    {
        var coverage = HistoryCoverage.Of(Hourly(8), Hour, T0);

        Assert.True(coverage.IsEnough);
        Assert.Equal(1d, coverage.Share, 2);
    }

    [Fact]
    public void A_span_shorter_than_seven_days_is_not_enough_however_dense()
    {
        var coverage = HistoryCoverage.Of(Hourly(6.5), Hour, T0);

        Assert.False(coverage.IsEnough);
        Assert.Contains("6.5 of the 7 days", coverage.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Seven_days_with_a_hole_are_not_enough_and_the_reason_shows_the_share()
    {
        // 30 days spanned, but the last week is only half there: the product
        // was down for three and a half days of it.
        var points = Hourly(30).Where(t => t < T0.AddDays(-5) || t > T0.AddDays(-1.5));

        var coverage = HistoryCoverage.Of(points, Hour, T0);

        Assert.False(coverage.IsEnough);
        Assert.Equal("49% of the 7 days read, 80% needed", coverage.Reason);
    }

    [Fact]
    public void Eighty_percent_is_enough_and_seventy_nine_is_not()
    {
        // 168 hourly points in seven days; 135 is 80.4%, 132 is 78.6% (shown rounded down).
        var span = Hourly(10).Where(t => t < T0.AddDays(-7)).ToList();

        var enough = HistoryCoverage.Of([.. span, .. Hourly(7).Take(135)], Hour, T0);
        var short_ = HistoryCoverage.Of([.. span, .. Hourly(7).Take(132)], Hour, T0);

        Assert.True(enough.IsEnough);
        Assert.False(short_.IsEnough);
        Assert.Equal("78% of the 7 days read, 80% needed", short_.Reason);
    }

    [Fact]
    public void The_expected_count_follows_the_series_interval()
    {
        // Five-minute buckets: 2016 expected in seven days, so hourly points
        // are an eighth of what should be there.
        var coverage = HistoryCoverage.Of(Hourly(8), TimeSpan.FromMinutes(5), T0);

        Assert.False(coverage.IsEnough);
        Assert.Equal(2016, coverage.PointsExpected);
    }

    [Fact]
    public void No_points_is_not_enough()
    {
        var coverage = HistoryCoverage.Of([], Hour, T0);

        Assert.False(coverage.IsEnough);
        Assert.Equal("the history spans 0 of the 7 days needed; 0% of the 7 days read, 80% needed", coverage.Reason);
    }

    [Fact]
    public void Duplicate_bucket_times_count_once()
    {
        // The N+1 trend sums hosts per bucket; two hosts must not read as twice
        // the coverage.
        var doubled = Hourly(8).Concat(Hourly(8));

        Assert.Equal(1d, HistoryCoverage.Of(doubled, Hour, T0).Share, 2);
    }

    [Fact]
    public void The_thresholds_are_product_policy()
    {
        var policy = HistoryCoveragePolicy.Default with { MinimumShare = 0.4 };
        var points = Hourly(30).Where(t => t < T0.AddDays(-5) || t > T0.AddDays(-1.5));

        Assert.True(HistoryCoverage.Of(points, Hour, T0, policy).IsEnough);
        Assert.Equal(TimeSpan.FromDays(7), HistoryCoveragePolicy.Default.MinimumSpan);
        Assert.Equal(0.8, HistoryCoveragePolicy.Default.MinimumShare);
    }
}
