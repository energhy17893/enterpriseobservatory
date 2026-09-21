namespace EnterpriseObservatory.Application.Analysis;

/// <summary>One reading of a series: when, and how much.</summary>
public readonly record struct TrendPoint(DateTimeOffset AtUtc, double Value);

/// <summary>The span of time an estimate was computed over, first point to last.</summary>
/// <remarks>
/// Carried on every answer, because a days-to-full without the window it came
/// from is not a number: ten days of growth and ninety days of growth that
/// agree on a slope are not equally worth believing.
/// </remarks>
public readonly record struct TrendWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc)
{
    public TimeSpan Span => ToUtc - FromUtc;
}

/// <summary>
/// A Theil–Sen line: slope per day, and the level it passes through at
/// <see cref="OriginUtc"/>.
/// </summary>
public readonly record struct TheilSenFit(double SlopePerDay, double Intercept, DateTimeOffset OriginUtc)
{
    /// <summary>The line's value at <paramref name="atUtc"/>.</summary>
    public double ValueAt(DateTimeOffset atUtc) =>
        Intercept + SlopePerDay * (atUtc - OriginUtc).TotalDays;
}

/// <summary>
/// The Mann–Kendall statistic of a series, with its tie-corrected variance and
/// the two-sided p-value of the normal approximation.
/// </summary>
/// <param name="S">Concordant minus discordant pairs, in time order.</param>
/// <param name="Variance">Var(S) under "no trend", corrected for tied values.</param>
/// <param name="Z">(S − 1)/√Var for S &gt; 0, (S + 1)/√Var for S &lt; 0, zero otherwise.</param>
/// <param name="PValue">Two-sided: the chance of an |S| this large with no trend at all.</param>
public readonly record struct MannKendallResult(long S, double Variance, double Z, double PValue)
{
    /// <summary>Whether a monotonic trend is present at significance <paramref name="alpha"/>.</summary>
    public bool IsSignificant(double alpha) => PValue < alpha;
}

/// <summary>
/// The trend arithmetic time-to-full is built on: Mann–Kendall to decide
/// whether a trend exists, Theil–Sen to say how steep it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this pair.</b> Both are built on the same object — every pair of
/// points in time order. Mann–Kendall counts the signs of the pairwise
/// differences; Theil–Sen takes the median of the pairwise slopes. If more
/// pairs rise than fall (S &gt; 0), more than half the non-zero slopes are
/// positive, so the median slope cannot be negative; and the other way round.
/// The gate and the estimate are therefore sign-consistent by construction:
/// the pairing can never report a significant rise with a falling forecast.
/// </para>
/// <para>
/// <b>Why not least squares.</b> Theil–Sen's breakdown point is about 29%: up
/// to that share of points can be arbitrarily wrong and the slope stays
/// bounded. One least-squares outlier moves the line. Stored buckets carry
/// min/max/sum/count/last only (ADR-0012), so no percentile-based smoothing is
/// available upstream either; robustness has to come from here.
/// </para>
/// <para>
/// <b>The limitation, stated rather than hidden.</b> Mann–Kendall assumes the
/// residuals are serially independent. Capacity usage is anything but: today's
/// usage is yesterday's plus a little. Positive autocorrelation makes the true
/// variance of S larger than the formula says, so the test is liberal — it
/// calls trends significant more readily than its nominal α. Corrections exist
/// (Hamed–Rao's effective sample size, Yue–Wang pre-whitening) and are not
/// implemented here. The practical consequence: significance is a necessary
/// gate, not a sufficient guarantee, and the other refusals (too few points,
/// too short a window, a step) carry weight of their own.
/// </para>
/// <para>
/// Cost is O(n²) in pairs: 720 hourly points are ~260 000 pairs, which is fine
/// for a per-datastore evaluation; much denser input should be bucketed first.
/// </para>
/// </remarks>
public static class Trend
{
    /// <summary>
    /// Median of all pairwise slopes, per day; intercept is the median of the
    /// residual levels, at the first point's time.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Fewer than two points, a non-finite value, or two points at the same time.
    /// </exception>
    public static TheilSenFit TheilSen(IReadOnlyList<TrendPoint> points)
    {
        var ordered = Ordered(points, minimum: 2);
        var origin = ordered[0].AtUtc;
        var x = ordered.Select(p => (p.AtUtc - origin).TotalDays).ToArray();
        var y = ordered.Select(p => p.Value).ToArray();

        var slopes = new List<double>(x.Length * (x.Length - 1) / 2);
        for (var i = 0; i < x.Length - 1; i++)
        {
            for (var j = i + 1; j < x.Length; j++)
            {
                slopes.Add((y[j] - y[i]) / (x[j] - x[i]));
            }
        }

        var slope = Stats.Median(slopes);
        var intercept = Stats.Median(y.Select((v, i) => v - slope * x[i]).ToList());

        return new TheilSenFit(slope, intercept, origin);
    }

    /// <summary>
    /// Mann–Kendall S over the series in time order, its variance with the tie
    /// correction, and the two-sided p-value of the continuity-corrected normal
    /// approximation.
    /// </summary>
    /// <remarks>
    /// Var(S) = [n(n−1)(2n+5) − Σ t(t−1)(2t+5)] / 18, the sum over groups of t
    /// equal values. The normal approximation is the usual one and is the
    /// reason time-to-full asks for a minimum number of points before it asks
    /// this question at all. Assumes serial independence — see
    /// <see cref="Trend"/>.
    /// </remarks>
    public static MannKendallResult MannKendall(IReadOnlyList<TrendPoint> points)
    {
        var y = Ordered(points, minimum: 2).Select(p => p.Value).ToArray();
        var n = y.Length;

        long s = 0;
        for (var i = 0; i < n - 1; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                s += Math.Sign(y[j] - y[i]);
            }
        }

        double tieTerm = y
            .GroupBy(v => v)
            .Select(g => (double)g.Count())
            .Where(t => t > 1)
            .Sum(t => t * (t - 1) * (2 * t + 5));

        var variance = ((double)n * (n - 1) * (2 * n + 5) - tieTerm) / 18d;

        var z = variance <= 0 || s == 0
            ? 0d
            : (s > 0 ? s - 1 : s + 1) / Math.Sqrt(variance);

        var p = 2d * (1d - StandardNormalCdf(Math.Abs(z)));

        return new MannKendallResult(s, variance, z, Math.Clamp(p, 0d, 1d));
    }

    /// <summary>Φ(z), from erfc (Abramowitz &amp; Stegun 7.1.26, |error| &lt; 1.5e−7).</summary>
    /// <remarks>Public so the p-value's arithmetic is pinned by tests of its own.</remarks>
    public static double StandardNormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2d);
        var t = 1d / (1d + 0.3275911 * x);
        var erfc = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741
            + t * (-1.453152027 + t * 1.061405429)))) * Math.Exp(-x * x);

        return z >= 0 ? 1d - erfc / 2d : erfc / 2d;
    }

    /// <summary>
    /// The points in time order, refusing input no line can be drawn through.
    /// </summary>
    internal static TrendPoint[] Ordered(IReadOnlyList<TrendPoint> points, int minimum)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < minimum)
        {
            throw new ArgumentException($"At least {minimum} points are needed.", nameof(points));
        }

        var ordered = points.OrderBy(p => p.AtUtc).ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            if (!double.IsFinite(ordered[i].Value))
            {
                throw new ArgumentException($"Point at {ordered[i].AtUtc:O} is not a finite number.", nameof(points));
            }

            if (i > 0 && ordered[i].AtUtc == ordered[i - 1].AtUtc)
            {
                throw new ArgumentException($"Two points at {ordered[i].AtUtc:O}.", nameof(points));
            }
        }

        return ordered;
    }
}
