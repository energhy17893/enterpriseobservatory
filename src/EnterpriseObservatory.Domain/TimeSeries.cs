namespace EnterpriseObservatory.Domain;

/// <summary>
/// How finely a series is kept.
/// </summary>
/// <remarks>
/// Keeping every sample at full resolution forever is not an option and never
/// was: one mid-sized estate produces tens of millions of samples a day. Every
/// monitoring product answers this the same way — keep recent data fine and old
/// data coarse — and the only real decisions are how coarse, how long, and what
/// is lost on the way.
/// </remarks>
public enum SeriesResolution
{
    /// <summary>Exactly what the collector reported.</summary>
    Raw = 0,

    /// <summary>Five-minute buckets.</summary>
    FiveMinutes = 1,

    /// <summary>One-hour buckets.</summary>
    OneHour = 2,
}

/// <summary>What identifies one series.</summary>
/// <param name="Entity">The thing being measured.</param>
/// <param name="Counter">The counter, as the metric contract names it.</param>
/// <param name="Instance">
/// The device, or empty for the aggregate across devices. Kept in the key
/// because an average across paths can hide one sick path behind eleven healthy
/// ones — see <see cref="CounterValue.Instance"/>.
/// </param>
public readonly record struct SeriesKey(EntityId Entity, string Counter, string Instance)
{
    public bool IsAggregateInstance => string.IsNullOrEmpty(Instance);
}

/// <summary>
/// One bucket of a downsampled series.
/// </summary>
/// <remarks>
/// <para>
/// Five numbers rather than one, and the choice is the whole point of this
/// type. Storing only the average is the classic way a monitoring product
/// loses the thing it exists to show: a host pinned at 100% for two minutes
/// inside an hour averages to about 3% and disappears completely.
/// </para>
/// <para>
/// They are also chosen so that a coarser bucket can be built from finer ones
/// without going back to the raw samples — min of mins, max of maxes, sum of
/// sums, count of counts. That is why <see cref="Sum"/> and
/// <see cref="Count"/> are stored instead of an average: an average cannot be
/// re-aggregated without knowing how many samples it came from.
/// </para>
/// </remarks>
public readonly record struct AggregatedSample
{
    public required DateTimeOffset StartUtc { get; init; }

    public required double Min { get; init; }

    public required double Max { get; init; }

    public required double Sum { get; init; }

    public required int Count { get; init; }

    /// <summary>
    /// The last sample in the bucket.
    /// </summary>
    /// <remarks>
    /// Kept for <see cref="RollupType.Latest"/> counters, where averaging is
    /// meaningless: the average of a datastore's free space over an hour is not
    /// a quantity anybody asked about, and the current figure is.
    /// </remarks>
    public required double Last { get; init; }

    /// <summary>The mean, when the counter is one a mean makes sense for.</summary>
    public double Average => Count == 0 ? 0 : Sum / Count;

    /// <summary>Folds two buckets of the same series into one coarser bucket.</summary>
    public AggregatedSample Merge(AggregatedSample later) => new()
    {
        StartUtc = StartUtc <= later.StartUtc ? StartUtc : later.StartUtc,
        Min = Math.Min(Min, later.Min),
        Max = Math.Max(Max, later.Max),
        Sum = Sum + later.Sum,
        Count = Count + later.Count,
        // The later bucket's last sample is the later sample.
        Last = StartUtc <= later.StartUtc ? later.Last : Last,
    };
}

/// <summary>Facts about resolutions that both storage and queries depend on.</summary>
public static class SeriesResolutions
{
    /// <summary>
    /// The nominal width of one bucket.
    /// </summary>
    /// <remarks>
    /// <see cref="SeriesResolution.Raw"/> has no fixed width — it is whatever
    /// the platform sampled, twenty seconds on a real-time feed and five
    /// minutes on a historical one. The figure here is the sampling interval
    /// the product asks for, used only to estimate how many points a range will
    /// produce. See ADR-0005.
    /// </remarks>
    public static TimeSpan Width(SeriesResolution resolution) => resolution switch
    {
        SeriesResolution.Raw => TimeSpan.FromSeconds(30),
        SeriesResolution.FiveMinutes => TimeSpan.FromMinutes(5),
        SeriesResolution.OneHour => TimeSpan.FromHours(1),
        _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
    };

    /// <summary>Where a timestamp's bucket begins, for a given resolution.</summary>
    public static DateTimeOffset BucketStart(DateTimeOffset atUtc, SeriesResolution resolution)
    {
        var width = Width(resolution).Ticks;
        var utc = atUtc.ToUniversalTime();

        return new DateTimeOffset(utc.Ticks - (utc.Ticks % width), TimeSpan.Zero);
    }

    /// <summary>Which finer resolution a coarser one is built from.</summary>
    /// <remarks>
    /// One hour is folded from the five-minute buckets rather than from raw
    /// samples, because by then the raw samples are gone. That is only sound
    /// because the stored aggregates re-aggregate exactly — see
    /// <see cref="AggregatedSample"/>.
    /// </remarks>
    public static SeriesResolution? Source(SeriesResolution resolution) => resolution switch
    {
        SeriesResolution.FiveMinutes => SeriesResolution.Raw,
        SeriesResolution.OneHour => SeriesResolution.FiveMinutes,
        _ => null,
    };
}

/// <summary>How long each resolution is kept.</summary>
/// <remarks>
/// <para>
/// Two days of raw covers "what exactly happened at 03:14" while the incident
/// is still being investigated. A month of five-minute data covers "did this
/// start when we patched". Ninety days of hourly data covers capacity
/// planning: enough of a base to project a trend and say "we need more hosts"
/// as an argument rather than a feeling.
/// </para>
/// <para>
/// The hourly window was 400 days, chosen so that this December could be
/// compared with last December. That comparison is no longer possible and the
/// trade was made deliberately — see ADR-0017, which also records why the
/// original arithmetic was wrong.
/// </para>
/// <para>
/// The cost does <em>not</em> fall tenfold per step, whatever the tenfold drop
/// in sample rate suggests. Over the configured windows each tier holds more
/// rows than the one before it, because the coarser tier is kept fifteen and
/// (formerly two hundred) times longer:
/// </para>
/// <code>
/// raw       2880/day x   2 days = 5,760 rows per series
/// 5-minute   288/day x  30 days = 8,640 rows per series
/// hourly      24/day x  90 days = 2,160 rows per series
/// </code>
/// <para>
/// At 400 days the hourly tier was 9,600 rows per series and the largest of
/// the three. At 90 it is the smallest, and the long tail is genuinely cheap
/// for the first time rather than by assertion.
/// </para>
/// </remarks>
public sealed record SeriesRetentionPolicy
{
    public TimeSpan Raw { get; init; } = TimeSpan.FromDays(2);

    public TimeSpan FiveMinutes { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Ninety days. See the remarks on this type, and ADR-0017.</summary>
    public TimeSpan OneHour { get; init; } = TimeSpan.FromDays(90);

    public static SeriesRetentionPolicy Default { get; } = new();

    public TimeSpan For(SeriesResolution resolution) => resolution switch
    {
        SeriesResolution.Raw => Raw,
        SeriesResolution.FiveMinutes => FiveMinutes,
        SeriesResolution.OneHour => OneHour,
        _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
    };

    /// <summary>
    /// How long after a bucket ends before it is considered complete.
    /// </summary>
    /// <remarks>
    /// A bucket that is still receiving samples must not be summarised, or the
    /// summary is of half a bucket and will not be revisited. The grace period
    /// covers a collection cycle running late.
    /// </remarks>
    public TimeSpan CompactionGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The finest resolution that answers a range without flooding the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chosen here rather than asked for, because a client that asked for raw
    /// data over thirty days would be asking for eighty thousand points it
    /// cannot draw and we may no longer have.
    /// </para>
    /// <para>
    /// The resolution actually used is reported back. A chart that does not say
    /// it is showing hourly averages reads as a live measurement, which is
    /// precisely the kind of quiet misrepresentation product principle 1 is
    /// about.
    /// </para>
    /// </remarks>
    public static SeriesResolution ResolutionFor(TimeSpan range, int maxPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPoints);

        foreach (var resolution in (ReadOnlySpan<SeriesResolution>)
            [SeriesResolution.Raw, SeriesResolution.FiveMinutes, SeriesResolution.OneHour])
        {
            if (range <= SeriesResolutions.Width(resolution) * maxPoints)
            {
                return resolution;
            }
        }

        return SeriesResolution.OneHour;
    }

    /// <summary>
    /// The finest resolution that answers a range and is still <em>kept</em>.
    /// </summary>
    /// <param name="range">How wide the window is.</param>
    /// <param name="maxPoints">How many points the caller can use.</param>
    /// <param name="ageOfOldestPoint">
    /// How long ago the start of the window was. A window is only answerable
    /// from a tier whose retention still reaches back that far.
    /// </param>
    /// <remarks>
    /// <para>
    /// <see cref="ResolutionFor(TimeSpan, int)"/> chooses from the window's
    /// <em>width</em> alone, so an hour-wide window picks Raw whether it is
    /// this hour or one from a fortnight ago. Raw is kept two days and
    /// five-minute data thirty, so for twenty-eight of those thirty days the
    /// answer is in the database and the question cannot reach it. The caller
    /// gets an existing series with no points, which the interface renders as
    /// "Nothing was recorded in this window" — a sentence its own comment says
    /// is usually a collector problem. It is not.
    /// </para>
    /// <para>
    /// Changing resolution is already a first-class, visible act: the chosen
    /// one travels back on <see cref="SeriesResult.Resolution"/> and the chart
    /// prints it. So answering a stale window from a coarser tier is not a
    /// quiet substitution, as long as it is reported — which is what makes
    /// this fixable here rather than needing a new concept.
    /// </para>
    /// </remarks>
    public SeriesResolution RetainedResolutionFor(
        TimeSpan range, int maxPoints, TimeSpan ageOfOldestPoint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPoints);

        foreach (var resolution in (ReadOnlySpan<SeriesResolution>)
            [SeriesResolution.Raw, SeriesResolution.FiveMinutes, SeriesResolution.OneHour])
        {
            // Both conditions, not either. A tier has to be fine enough to
            // answer within the caller's point budget and still be kept long
            // enough to reach the start of the window; one without the other
            // is how the question and the answer stopped meeting.
            if (range <= SeriesResolutions.Width(resolution) * maxPoints &&
                ageOfOldestPoint <= For(resolution))
            {
                return resolution;
            }
        }

        // Nothing reaches. The coarsest tier is returned rather than a null or
        // a throw, so the caller gets the same shape of answer it always gets
        // and the emptiness is data rather than an exception — the window is
        // genuinely older than anything retained, and saying so with an empty
        // result at a stated resolution is the honest version of that.
        return SeriesResolution.OneHour;
    }
}
