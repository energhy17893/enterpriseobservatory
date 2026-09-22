using System.Globalization;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// How much history is "enough" to say something that rests on a trend.
/// </summary>
/// <remarks>
/// <b>Product policy, not a citation</b> (ADR-0026, planner decision): seven
/// days so a quiet weekend is never extrapolated as the working week, and 80%
/// of the points those seven days should hold so a series with a hole in it is
/// not read as a whole one. Below either, the answer is "not evaluated" with the
/// number, never a pass or a date.
/// </remarks>
public sealed record HistoryCoveragePolicy
{
    /// <summary>The span the history must reach, and the window the share is counted over.</summary>
    public TimeSpan MinimumSpan { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The share of the expected points in <see cref="MinimumSpan"/> that must have been read.</summary>
    public double MinimumShare { get; init; } = 0.8;

    public static HistoryCoveragePolicy Default { get; } = new();
}

/// <summary>
/// Whether a series has enough history behind it: the one helper both N+1
/// (compliance) and datastore time-to-full judge by.
/// </summary>
/// <remarks>
/// <para>
/// Two conditions, and neither implies the other. The span — first point to
/// last — must reach <see cref="HistoryCoveragePolicy.MinimumSpan"/>: a dense
/// three days is still three days. And in the most recent
/// <see cref="HistoryCoveragePolicy.MinimumSpan"/> ending now, at least
/// <see cref="HistoryCoveragePolicy.MinimumShare"/> of the points the series'
/// interval should have produced must be there: thirty days spanned with the
/// last week half missing is not a week of evidence.
/// </para>
/// <para>
/// Points are counted by distinct time, so an aggregate built from several
/// series (the N+1 trend sums hosts per bucket) is not counted as more coverage
/// than it has.
/// </para>
/// </remarks>
public sealed record HistoryCoverage
{
    /// <summary>First point to last.</summary>
    public required TimeSpan Span { get; init; }

    /// <summary>Distinct points in the last <see cref="HistoryCoveragePolicy.MinimumSpan"/>.</summary>
    public required int PointsRead { get; init; }

    /// <summary>What that window holds at the series' interval.</summary>
    public required int PointsExpected { get; init; }

    public required HistoryCoveragePolicy Policy { get; init; }

    /// <summary><see cref="PointsRead"/> over <see cref="PointsExpected"/>, at most 1.</summary>
    public double Share => PointsExpected == 0 ? 0 : Math.Min(1d, (double)PointsRead / PointsExpected);

    public bool IsEnough => Span >= Policy.MinimumSpan && Share >= Policy.MinimumShare;

    /// <summary>
    /// Why it is not enough, with the number: "X% of the 7 days read, 80% needed".
    /// Empty when it is enough.
    /// </summary>
    /// <remarks>The share is rounded down, so a 79.9% series never reads as the 80% it missed.</remarks>
    public string Reason
    {
        get
        {
            if (IsEnough)
            {
                return string.Empty;
            }

            var days = Policy.MinimumSpan.TotalDays;
            var share = string.Create(CultureInfo.InvariantCulture,
                $"{Math.Floor(Share * 100):0}% of the {days:0.#} days read, {Policy.MinimumShare * 100:0.#}% needed");

            return Span >= Policy.MinimumSpan
                ? share
                : string.Create(CultureInfo.InvariantCulture,
                    $"the history spans {Span.TotalDays:0.#} of the {days:0.#} days needed; {share}");
        }
    }

    /// <summary>Judges the points of one series (or one aggregate) read at <paramref name="interval"/>.</summary>
    /// <param name="points">When each point is; any order, duplicates allowed.</param>
    /// <param name="interval">The width of one point: the tier the series was read from.</param>
    /// <param name="nowUtc">The end of the window the share is counted over.</param>
    /// <param name="policy">The thresholds; <see cref="HistoryCoveragePolicy.Default"/> when null.</param>
    public static HistoryCoverage Of(
        IEnumerable<DateTimeOffset> points,
        TimeSpan interval,
        DateTimeOffset nowUtc,
        HistoryCoveragePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        var rules = policy ?? HistoryCoveragePolicy.Default;
        var distinct = points.Distinct().Order().ToList();
        var from = nowUtc - rules.MinimumSpan;

        return new HistoryCoverage
        {
            Span = distinct.Count > 1 ? distinct[^1] - distinct[0] : TimeSpan.Zero,
            PointsRead = distinct.Count(t => t > from && t <= nowUtc),
            PointsExpected = (int)Math.Ceiling(rules.MinimumSpan / interval),
            Policy = rules,
        };
    }
}
