namespace EnterpriseObservatory.Application.Analysis;

/// <summary>Why time-to-full declined to give a date. Machine-readable; the text is in the detail.</summary>
public enum TimeToFullRefusalReason
{
    /// <summary>Fewer points than <see cref="TimeToFullPolicy.MinimumPoints"/>.</summary>
    TooFewPoints,

    /// <summary>The points span less than <see cref="TimeToFullPolicy.MinimumWindow"/>.</summary>
    WindowTooShort,

    /// <summary>The latest reading is already at or above capacity.</summary>
    AlreadyFull,

    /// <summary>Usage is below <see cref="TimeToFullPolicy.UsageFloorFraction"/> of capacity.</summary>
    BelowUsageFloor,

    /// <summary>Mann–Kendall does not find a monotonic trend at <see cref="TimeToFullPolicy.Significance"/>.</summary>
    NoSignificantTrend,

    /// <summary>One level shift dominates the series: a step, not a trend.</summary>
    StepChange,

    /// <summary>The trend is flat or shrinking: at this rate it never fills.</summary>
    NotFilling,

    /// <summary>It fills, but later than <see cref="TimeToFullPolicy.Horizon"/>.</summary>
    BeyondHorizon,

    /// <summary>
    /// Too little of the history was read: <see cref="HistoryCoverage"/>'s
    /// seven days and 80% of their points (ADR-0026).
    /// </summary>
    InsufficientHistory,
}

/// <summary>
/// The thresholds time-to-full refuses by. Each default says where it came
/// from: borrowed from a named product, or ours and open to measurement.
/// </summary>
public sealed record TimeToFullPolicy
{
    /// <summary>
    /// Fewest points to estimate from. Default 14 — <b>borrowed</b> from
    /// Dynatrace, the one published minimum for a capacity forecast we are
    /// aware of. It also keeps Mann–Kendall's normal approximation (usually
    /// quoted as fine from n ≈ 10) on safe ground.
    /// </summary>
    public int MinimumPoints { get; init; } = 14;

    /// <summary>
    /// Shortest span the points must cover. Default 7 days — <b>ours</b>: one
    /// weekly cycle, so a working week's growth is not extrapolated as if
    /// weekends did not exist.
    /// </summary>
    public TimeSpan MinimumWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Mann–Kendall two-sided significance level. Default 0.05 — the statistical
    /// convention, <b>ours</b> to adopt. Liberal on autocorrelated data; see
    /// <see cref="Trend"/>.
    /// </summary>
    public double Significance { get; init; } = 0.05;

    /// <summary>
    /// Furthest ahead a date is given. Default 365 days — <b>ours</b>. Beyond it
    /// the answer is "not within a year", not a date: a slope measured over
    /// weeks says little about next year.
    /// </summary>
    public TimeSpan Horizon { get; init; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Points each side of a candidate step whose medians are compared.
    /// Default 3 — <b>ours</b>: the smallest window whose median ignores one
    /// stray point.
    /// </summary>
    public int StepWindowPoints { get; init; } = 3;

    /// <summary>
    /// Share of the series' whole change one level shift must reach to be
    /// called a step. Default 0.5 — <b>ours</b>: when a single jump is at least
    /// half of all the growth, the "trend" is mostly that jump.
    /// </summary>
    public double StepShareOfChange { get; init; } = 0.5;

    /// <summary>
    /// Optional: refuse while usage is below this fraction of capacity. Off by
    /// default. The idea is <b>borrowed</b> from the Prometheus monitoring
    /// mixins, which only evaluate their fill-up prediction once free space is
    /// already below a threshold (node-mixin's filesystem warning: under 40%
    /// free, i.e. a floor of 0.6) so that a nearly empty volume with a steep
    /// slope does not page anyone.
    /// </summary>
    public double? UsageFloorFraction { get; init; }

    /// <summary>The defaults above.</summary>
    public static TimeToFullPolicy Default { get; } = new();

    internal void Validate()
    {
        if (StepWindowPoints < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(StepWindowPoints), "Must be at least 1.");
        }

        if (MinimumPoints < Math.Max(3, 2 * StepWindowPoints))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumPoints), "Must be at least 3 and at least two step windows.");
        }

        if (Significance is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Significance), "Must be between 0 and 1.");
        }

        if (StepShareOfChange is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(StepShareOfChange), "Must be in (0, 1].");
        }

        if (Horizon <= TimeSpan.Zero || MinimumWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Horizon), "Horizon must be positive, window non-negative.");
        }

        if (UsageFloorFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(UsageFloorFraction), "Must be between 0 and 1.");
        }
    }
}

/// <summary>Either a date with its window, or a reason there is none.</summary>
public abstract record TimeToFullResult
{
    private TimeToFullResult() { }

    /// <summary>Points the answer was computed from.</summary>
    public required int PointsUsed { get; init; }

    /// <summary>
    /// Fills at <see cref="FullAtUtc"/>, <see cref="Days"/> from now, rising
    /// <see cref="SlopePerDay"/> a day as measured over <see cref="Window"/>.
    /// </summary>
    public sealed record Forecast : TimeToFullResult
    {
        public required DateTimeOffset FullAtUtc { get; init; }

        /// <summary>Days from the evaluation time to <see cref="FullAtUtc"/>; never negative.</summary>
        public required double Days { get; init; }

        /// <summary>Theil–Sen slope, in the series' unit per day.</summary>
        public required double SlopePerDay { get; init; }

        public required TrendWindow Window { get; init; }

        /// <summary>Mann–Kendall two-sided p-value of the trend the date rests on.</summary>
        public required double PValue { get; init; }
    }

    /// <summary>No date, and why.</summary>
    public sealed record Refusal : TimeToFullResult
    {
        public required TimeToFullRefusalReason Reason { get; init; }

        /// <summary>A sentence for a person; <see cref="Reason"/> is for code.</summary>
        public required string Detail { get; init; }

        /// <summary>The span looked at; absent only when there were no points.</summary>
        public TrendWindow? Window { get; init; }

        /// <summary>The slope, when it got as far as computing one.</summary>
        public double? SlopePerDay { get; init; }
    }
}

/// <summary>
/// When a growing quantity reaches its capacity — or an explicit refusal.
/// </summary>
/// <remarks>
/// <para>
/// Refuse rather than guess. The order of checks is part of the contract:
/// </para>
/// <list type="number">
/// <item>too few points; window too short (not enough evidence to ask);</item>
/// <item>already full; below the usage floor (the question does not apply);</item>
/// <item>no significant Mann–Kendall trend — unless Theil–Sen is exactly
/// flat, which is reported as <see cref="TimeToFullRefusalReason.NotFilling"/>;</item>
/// <item>a step change (a significant "trend" that is really one jump);</item>
/// <item>flat or shrinking;</item>
/// <item>beyond the horizon.</item>
/// </list>
/// <para>
/// The step check sits after significance on purpose: on pure noise the
/// medians either side of any boundary differ by noise, the whole-series
/// change is also noise, and their ratio means nothing — that series is
/// "no trend", not "a step". A step, on the other hand, is always
/// significant to Mann–Kendall (it is perfectly monotonic across the jump),
/// and must be caught before its Theil–Sen slope — which may be anything
/// from zero to the jump spread over the window — is extrapolated.
/// </para>
/// <para>
/// The date comes from the Theil–Sen line evaluated at <c>now</c>, not from
/// the last reading, so a single odd last point does not move it; "already
/// full" is judged on the last reading, since that is what is on disk.
/// </para>
/// </remarks>
public static class TimeToFull
{
    public static TimeToFullResult Estimate(
        IReadOnlyList<TrendPoint> points,
        double capacity,
        DateTimeOffset now,
        TimeToFullPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        policy ??= TimeToFullPolicy.Default;
        policy.Validate();

        if (!double.IsFinite(capacity) || capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be a positive number.");
        }

        if (points.Count == 0)
        {
            return Refuse(TimeToFullRefusalReason.TooFewPoints, 0, null,
                $"No points; at least {policy.MinimumPoints} are needed.");
        }

        var ordered = Trend.Ordered(points, minimum: 1);
        var n = ordered.Length;
        var window = new TrendWindow(ordered[0].AtUtc, ordered[^1].AtUtc);
        var last = ordered[^1].Value;

        if (n < policy.MinimumPoints)
        {
            return Refuse(TimeToFullRefusalReason.TooFewPoints, n, window,
                $"{n} points; at least {policy.MinimumPoints} are needed.");
        }

        if (window.Span < policy.MinimumWindow)
        {
            return Refuse(TimeToFullRefusalReason.WindowTooShort, n, window,
                $"The points span {window.Span.TotalDays:0.#} days; at least {policy.MinimumWindow.TotalDays:0.#} are needed.");
        }

        if (last >= capacity)
        {
            return Refuse(TimeToFullRefusalReason.AlreadyFull, n, window,
                $"The latest reading is at or above capacity.");
        }

        if (policy.UsageFloorFraction is { } floor && last < floor * capacity)
        {
            return Refuse(TimeToFullRefusalReason.BelowUsageFloor, n, window,
                $"Usage is {last / capacity:P0} of capacity, below the {floor:P0} floor.");
        }

        var mk = Trend.MannKendall(ordered);
        var fit = Trend.TheilSen(ordered);

        if (!mk.IsSignificant(policy.Significance))
        {
            return fit.SlopePerDay == 0
                ? Refuse(TimeToFullRefusalReason.NotFilling, n, window, $"Usage is flat; it never fills at this rate.", 0)
                : Refuse(TimeToFullRefusalReason.NoSignificantTrend, n, window,
                    $"No monotonic trend at significance {policy.Significance} (p = {mk.PValue:0.###}).",
                    fit.SlopePerDay);
        }

        if (DominantStep(ordered, policy) is { } step)
        {
            return Refuse(TimeToFullRefusalReason.StepChange, n, window,
                $"A single level shift of {step.Shift:0.##} near {step.AtUtc:O} is {step.Share:P0} of the whole change; that is a step, not a trend.",
                fit.SlopePerDay);
        }

        if (fit.SlopePerDay <= 0)
        {
            return Refuse(TimeToFullRefusalReason.NotFilling, n, window,
                $"Usage is {(fit.SlopePerDay == 0 ? "flat" : "shrinking")}; it never fills at this rate.",
                fit.SlopePerDay);
        }

        var days = Math.Max(0d, (capacity - fit.ValueAt(now)) / fit.SlopePerDay);

        if (days > policy.Horizon.TotalDays)
        {
            return Refuse(TimeToFullRefusalReason.BeyondHorizon, n, window,
                $"Not within {policy.Horizon.TotalDays:0} days at {fit.SlopePerDay:0.##} a day.",
                fit.SlopePerDay);
        }

        return new TimeToFullResult.Forecast
        {
            FullAtUtc = now + TimeSpan.FromDays(days),
            Days = days,
            SlopePerDay = fit.SlopePerDay,
            Window = window,
            PointsUsed = n,
            PValue = mk.PValue,
        };
    }

    private readonly record struct Step(double Shift, double Share, DateTimeOffset AtUtc);

    /// <summary>
    /// The largest level shift between two neighbouring runs of
    /// <see cref="TimeToFullPolicy.StepWindowPoints"/> points, if it is at least
    /// <see cref="TimeToFullPolicy.StepShareOfChange"/> of the whole change.
    /// </summary>
    /// <remarks>
    /// Deliberately simple: medians of the w points before and after each
    /// boundary, compared with the medians of the first and last w points. The
    /// medians make one stray reading invisible (a spike goes up and comes
    /// back; a step stays). It is not a change-point test — it answers only
    /// "does one jump explain most of this series?". Two adjacent outliers can
    /// still fool a window of three, and a steady linear series of n points
    /// shows a largest shift of about w/(n − w) of its change (3/11 ≈ 27% at
    /// the 14-point minimum), comfortably under the default half.
    /// </remarks>
    private static Step? DominantStep(TrendPoint[] ordered, TimeToFullPolicy policy)
    {
        var w = policy.StepWindowPoints;
        var y = ordered.Select(p => p.Value).ToArray();

        var whole = Median(y, y.Length - w, w) - Median(y, 0, w);

        var best = 0d;
        var bestAt = 0;
        for (var k = w; k <= y.Length - w; k++)
        {
            var shift = Median(y, k, w) - Median(y, k - w, w);
            if (Math.Abs(shift) > Math.Abs(best))
            {
                best = shift;
                bestAt = k;
            }
        }

        if (best == 0)
        {
            return null;
        }

        var share = whole == 0 ? double.PositiveInfinity : Math.Abs(best / whole);

        return share >= policy.StepShareOfChange
            ? new Step(best, share, ordered[bestAt].AtUtc)
            : null;
    }

    private static double Median(double[] values, int start, int count) =>
        Stats.Median(new ArraySegment<double>(values, start, count));

    private static TimeToFullResult.Refusal Refuse(
        TimeToFullRefusalReason reason,
        int pointsUsed,
        TrendWindow? window,
        FormattableString detail,
        double? slopePerDay = null) => new()
        {
            Reason = reason,
            Detail = FormattableString.Invariant(detail),
            Window = window,
            PointsUsed = pointsUsed,
            SlopePerDay = slopePerDay,
        };
}
