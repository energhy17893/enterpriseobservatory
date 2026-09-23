using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Monitoring;

/// <summary>What a caller wants out of a series.</summary>
public sealed record SeriesQuery
{
    public required SeriesKey Key { get; init; }

    public required DateTimeOffset FromUtc { get; init; }

    public required DateTimeOffset ToUtc { get; init; }

    /// <summary>
    /// How many points the caller can use.
    /// </summary>
    /// <remarks>
    /// Drives which resolution answers the query. A browser cannot draw more
    /// points than it has pixels, and a caller that asked for all of them would
    /// get a slow response it then threw away.
    /// </remarks>
    public int MaxPoints { get; init; } = 720;

    /// <summary>
    /// Forces a resolution instead of letting the range choose one.
    /// </summary>
    /// <remarks>
    /// For a caller that knows what it wants — an export, a test. It can return
    /// nothing if the data has aged out of that resolution, which is the honest
    /// answer.
    /// </remarks>
    public SeriesResolution? Resolution { get; init; }
}

/// <summary>A series as it was answered.</summary>
public sealed record SeriesResult
{
    public required SeriesKey Key { get; init; }

    /// <summary>
    /// The resolution actually used.
    /// </summary>
    /// <remarks>
    /// Always reported, and the interface is expected to show it. A chart that
    /// does not say it is drawing hourly averages reads as a live measurement.
    /// </remarks>
    public required SeriesResolution Resolution { get; init; }

    /// <summary>
    /// The points, oldest first.
    /// </summary>
    /// <remarks>
    /// Absent buckets are absent, never zero-filled. A gap means "we were not
    /// looking"; a zero means "we looked and it was nothing", and drawing the
    /// second when the first is true is how a monitoring product tells its
    /// first lie. See product principle 1.
    /// </remarks>
    public IReadOnlyList<AggregatedSample> Points { get; init; } = [];

    /// <summary>The counter's unit, so the caller need not guess.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>
    /// How the platform combined the samples, which decides what may be read
    /// off the bucket. See <see cref="RollupType"/>.
    /// </summary>
    public RollupType Rollup { get; init; }

    /// <summary>Whether points were dropped to keep the response bounded.</summary>
    public bool Truncated { get; init; }

    /// <summary>Whether the series is known at all.</summary>
    public bool Exists { get; init; }
}

/// <summary>
/// Where measurements go.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a different port from the state stores. Alert and graph state
/// is small, transactional and read-modify-write; measurements are
/// append-heavy, high-volume and range-queried. One decision covering both
/// serves both badly — see ADR-0012.
/// </para>
/// <para>
/// Writes never fail the cycle. A metric that could not be stored is a lost
/// data point; a collection loop that stopped because storage was busy is a
/// blind monitoring system, and the second is much worse than the first.
/// </para>
/// </remarks>
public interface IObservationStore : ISeriesReader
{
    /// <summary>Appends one cycle's samples.</summary>
    void Append(IReadOnlyList<Observation> observations);

    /// <summary>
    /// Folds complete buckets into coarser ones and deletes what has aged out.
    /// </summary>
    /// <returns>What the pass did, for reporting.</returns>
    /// <remarks>
    /// Idempotent. It runs on a timer, and a pass that was interrupted must be
    /// safe to repeat — which it is, because a bucket is computed from its
    /// sources rather than accumulated into.
    /// </remarks>
    CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy);
}

/// <summary>
/// The read side of <see cref="IObservationStore"/>, which is what analysis
/// rules are given.
/// </summary>
/// <remarks>
/// A rule must not write, delete or compact. See the analysis-layer proposal,
/// which named this port before any rule needed it.
/// </remarks>
public interface ISeriesReader
{
    /// <summary>Reads one series.</summary>
    SeriesResult Query(SeriesQuery query);

    /// <summary>Lists the counters recorded for an entity.</summary>
    /// <remarks>
    /// So the interface can offer what actually exists rather than a fixed menu
    /// that is wrong for half the entity types.
    /// </remarks>
    IReadOnlyList<SeriesKey> SeriesFor(EntityId entity);
}

/// <summary>What one compaction pass did.</summary>
public sealed record CompactionReport
{
    public int BucketsWritten { get; init; }

    public int SamplesDeleted { get; init; }

    public int BucketsDeleted { get; init; }

    /// <summary>
    /// How long both retention deletes took. Logged every pass so the week
    /// that decides whether <c>sample</c> needs an index on its age has its
    /// numbers.
    /// </summary>
    public TimeSpan DeleteDuration { get; init; }

    // There was a SeriesForgotten count here, for a sweep step that deleted
    // series rows with nothing left. The step is gone (see the note in
    // PostgresObservationStore.Compact and ADR-0019) and the field went with
    // it rather than staying as a zero. A number the product reports every
    // pass, about a thing the product no longer does, is a small lie it tells
    // about itself — and the operator reading "0 series forgotten" would have
    // no way to tell it apart from "nothing was eligible this time".

    public bool DidSomething =>
        BucketsWritten > 0 || SamplesDeleted > 0 || BucketsDeleted > 0;
}
