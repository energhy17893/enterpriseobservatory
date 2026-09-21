using EnterpriseObservatory.Application.Analysis;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The two statistics time-to-full stands on, pinned by examples small enough
/// to check by hand.
/// </summary>
public class TrendTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static TrendPoint[] Daily(params double[] values) =>
        values.Select((v, i) => new TrendPoint(T0.AddDays(i), v)).ToArray();

    // --- what an estimate costs ----------------------------------------------

    [Fact]
    public void Theil_sen_over_thirty_days_of_hourly_points_does_not_allocate_its_pairs()
    {
        // 720 points are 258,840 pairwise slopes. Held in a fresh list and
        // sorted into another for the median, that was over 4 MB per
        // datastore per estimate; 41 datastores every five minutes is a lot of
        // garbage for one number each.
        var random = new Random(7);
        var points = Enumerable.Range(0, 720)
            .Select(i => new TrendPoint(T0.AddHours(i), 1000 + i * 0.5 + random.NextDouble()))
            .ToArray();

        Trend.TheilSen(points); // warm: the pooled buffer is rented once

        var before = GC.GetAllocatedBytesForCurrentThread();
        Trend.TheilSen(points);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 256 * 1024, $"Theil–Sen allocated {allocated:N0} bytes.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(1000)]
    [InlineData(1001)]
    public void The_median_selected_in_place_is_exactly_the_sorted_median(int count)
    {
        // Duplicates on purpose: a flat datastore's slopes are all equal, and
        // that is the case a careless quickselect goes quadratic on.
        var random = new Random(count);
        var values = Enumerable.Range(0, count)
            .Select(_ => random.Next(4) == 0 ? 0d : Math.Round(random.NextDouble() * 50, 1))
            .ToArray();
        var copy = values.ToArray();

        var sorted = values.Order().ToList();
        var expected = count % 2 == 1
            ? sorted[count / 2]
            : (sorted[count / 2 - 1] + sorted[count / 2]) / 2d;

        Assert.Equal(expected, Stats.Median(values));
        Assert.Equal(copy, values); // the caller's list is not reordered
        Assert.Equal(expected, Stats.MedianInPlace(values.AsSpan()));
    }

    [Fact]
    public void The_median_of_many_equal_values_is_that_value()
    {
        Assert.Equal(3d, Stats.MedianInPlace(Enumerable.Repeat(3d, 258_840).ToArray()));
    }

    [Fact]
    public void Mann_kendall_s_and_tie_corrected_variance_by_hand()
    {
        // 1, 3, 2, 4, 4 — pairs in time order:
        //   from 1: 3 +, 2 +, 4 +, 4 +   → +4
        //   from 3: 2 −, 4 +, 4 +        → +1
        //   from 2: 4 +, 4 +             → +2
        //   from 4: 4 tie                →  0
        // S = 7.
        // Var = [n(n−1)(2n+5) − Σ t(t−1)(2t+5)] / 18
        //     = [5·4·15 − 2·1·9] / 18 = (300 − 18) / 18 = 282/18 ≈ 15.667
        // Z = (S − 1)/√Var = 6/3.958 ≈ 1.516; two-sided p ≈ 0.1296.
        var mk = Trend.MannKendall(Daily(1, 3, 2, 4, 4));

        Assert.Equal(7, mk.S);
        Assert.Equal(282d / 18d, mk.Variance, precision: 10);
        Assert.Equal(6d / Math.Sqrt(282d / 18d), mk.Z, precision: 10);
        Assert.Equal(0.1296, mk.PValue, tolerance: 0.001);
        Assert.False(mk.IsSignificant(0.05));
    }

    [Fact]
    public void Mann_kendall_without_ties_uses_the_plain_variance()
    {
        // 1, 2, 3: every pair rises, S = 3; Var = 3·2·11/18 = 66/18.
        var mk = Trend.MannKendall(Daily(1, 2, 3));

        Assert.Equal(3, mk.S);
        Assert.Equal(66d / 18d, mk.Variance, precision: 10);
    }

    [Fact]
    public void Mann_kendall_reads_the_series_in_time_order_not_list_order()
    {
        var points = Daily(1, 2, 3, 4, 5).Reverse().ToArray();

        Assert.Equal(10, Trend.MannKendall(points).S);
    }

    [Fact]
    public void Theil_sen_ignores_one_wild_point_that_pulls_least_squares()
    {
        // (0,1) (1,2) (2,3) (3,4) (4,50). Six of the ten pairwise slopes are 1;
        // the other four (12.25, 16, 23.5, 46) involve the wild point. The
        // median of ten is the mean of the 5th and 6th — both 1.
        var points = Daily(1, 2, 3, 4, 50);

        var fit = Trend.TheilSen(points);

        Assert.Equal(1d, fit.SlopePerDay, precision: 10);
        Assert.Equal(1d, fit.Intercept, precision: 10);
        Assert.Equal(6d, fit.ValueAt(T0.AddDays(5)), precision: 10);

        // Least squares on the same points: Σ(dx·dy)/Σdx² = 100/10 = 10,
        // ten times too steep.
        Assert.Equal(10d, LeastSquares.Slope(points), precision: 10);
    }

    [Fact]
    public void Standard_normal_cdf_matches_the_table()
    {
        Assert.Equal(0.5, Trend.StandardNormalCdf(0), precision: 6);
        Assert.Equal(0.975, Trend.StandardNormalCdf(1.959964), tolerance: 1e-6);
        Assert.Equal(0.025, Trend.StandardNormalCdf(-1.959964), tolerance: 1e-6);
    }

    [Fact]
    public void Gate_and_estimate_never_disagree_in_sign()
    {
        // The pairing's selling point, checked rather than asserted: over many
        // arbitrary series, whenever Mann–Kendall says "trend", Theil–Sen's
        // slope is zero or has the same sign as S.
        var random = new Random(20260921);
        var significant = 0;

        for (var run = 0; run < 500; run++)
        {
            var drift = random.NextDouble() * 2 - 1;
            var points = Enumerable.Range(0, 20)
                .Select(i => new TrendPoint(T0.AddDays(i), drift * i + random.NextDouble() * 10))
                .ToArray();

            var mk = Trend.MannKendall(points);
            if (!mk.IsSignificant(0.05))
            {
                continue;
            }

            significant++;
            var slope = Trend.TheilSen(points).SlopePerDay;
            Assert.True(slope == 0 || Math.Sign(slope) == Math.Sign(mk.S), $"S = {mk.S}, slope = {slope}");
        }

        Assert.True(significant > 50, "the check should exercise many significant series");
    }

    [Fact]
    public void Two_points_at_the_same_time_are_refused_as_input()
    {
        var points = new[] { new TrendPoint(T0, 1), new TrendPoint(T0, 2) };

        Assert.Throws<ArgumentException>(() => Trend.TheilSen(points));
    }

    [Fact]
    public void A_non_finite_value_is_refused_as_input()
    {
        Assert.Throws<ArgumentException>(() => Trend.MannKendall(Daily(1, double.NaN, 3)));
    }
}

/// <summary>Ordinary least squares, for tests to show what Theil–Sen is protecting against.</summary>
internal static class LeastSquares
{
    public static double Slope(IReadOnlyList<TrendPoint> points)
    {
        var origin = points.Min(p => p.AtUtc);
        var x = points.Select(p => (p.AtUtc - origin).TotalDays).ToArray();
        var y = points.Select(p => p.Value).ToArray();
        var mx = x.Average();
        var my = y.Average();

        return x.Zip(y, (a, b) => (a - mx) * (b - my)).Sum() / x.Sum(a => (a - mx) * (a - mx));
    }
}
