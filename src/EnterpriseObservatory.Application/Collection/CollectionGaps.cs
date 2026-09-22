using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>Where a source-level collection gap stands.</summary>
public enum CollectionGapState
{
    /// <summary>Still being filled, oldest slice first.</summary>
    Open,

    /// <summary>Every sample in it was read again.</summary>
    Filled,

    /// <summary>
    /// Closed with part or all of it past what the platform still keeps;
    /// <see cref="CollectionGap.LostBeforeUtc"/> says how much.
    /// </summary>
    Unrecoverable,
}

/// <summary>
/// A stretch of time a whole source was not read, and how far it has been read since.
/// </summary>
/// <remarks>
/// <para>
/// Source level only. This records a <em>service</em> outage — the product was
/// stopped, or could not reach the vCenter — not one entity's gap: a single
/// host disconnected for twenty minutes leaves no row here, and is covered only
/// as far as the live read's few samples back reach. See
/// docs/reference-approaches.md §10.3 and handover 3.
/// </para>
/// <para>
/// <see cref="FromUtc"/> is exclusive — it is the newest sample the store
/// already had — and <see cref="ToUtc"/> inclusive, the start of the live read
/// that noticed the gap. The same convention as vim25's own
/// <c>startTime</c>/<c>endTime</c>, so a slice is asked for exactly as it is
/// recorded.
/// </para>
/// </remarks>
public sealed record CollectionGap
{
    public long Id { get; init; }

    public required string SourceInstanceId { get; init; }

    /// <summary>The newest sample held before the gap. Exclusive.</summary>
    public required DateTimeOffset FromUtc { get; init; }

    /// <summary>Where the live read that noticed the gap began. Inclusive.</summary>
    public required DateTimeOffset ToUtc { get; init; }

    /// <summary>Everything in (<see cref="FromUtc"/>, this] has been read again or given up.</summary>
    public required DateTimeOffset FilledToUtc { get; init; }

    public required CollectionGapState State { get; init; }

    /// <summary>
    /// Samples at or before this were past the platform's retention when the
    /// fill reached them, and are not in the store. Null when nothing was lost.
    /// </summary>
    public DateTimeOffset? LostBeforeUtc { get; init; }

    public required DateTimeOffset OpenedAtUtc { get; init; }

    public DateTimeOffset? ClosedAtUtc { get; init; }
}

/// <summary>
/// Where the source-level gap record is kept.
/// </summary>
/// <remarks>
/// Rows are never deleted by the collector. A gap that could not be filled is
/// closed as <see cref="CollectionGapState.Unrecoverable"/> with how much was
/// lost, because a hole nobody can explain afterwards is the silent kind.
/// </remarks>
public interface ICollectionGapStore
{
    /// <summary>
    /// The newest stored sample of each of these entities, for the ones that have any.
    /// </summary>
    /// <remarks>
    /// Read once per source per process, to seed the high-water marks: the
    /// store's newest timestamp is the mark, so a restart does not forget
    /// where the history stops.
    /// </remarks>
    IReadOnlyDictionary<EntityId, DateTimeOffset> LatestSampleTimes(IReadOnlyCollection<EntityId> entities);

    /// <summary>A source's open gaps, oldest first.</summary>
    IReadOnlyList<CollectionGap> OpenGaps(string sourceInstanceId);

    /// <summary>Every gap recorded for a source, oldest first, open or closed.</summary>
    IReadOnlyList<CollectionGap> Gaps(string sourceInstanceId);

    /// <summary>Records a new open gap and returns it with its id.</summary>
    CollectionGap Open(CollectionGap gap);

    /// <summary>
    /// Writes a gap's progress. Never moves it back and never reopens a closed one.
    /// </summary>
    void Update(CollectionGap gap);

    /// <summary>
    /// How many gaps, across every source, are in each state right now.
    /// </summary>
    /// <remarks>
    /// For the health endpoint (Package D): an operator who was blind for four
    /// hours needs to see that in one place, not per source. Rows are never
    /// deleted (see the remarks on this interface), so the unrecoverable count
    /// is the running total of history nothing can fill — not just today's.
    /// </remarks>
    IReadOnlyDictionary<CollectionGapState, int> CountsByState();
}

/// <summary>Series a collector writes about itself rather than about the estate.</summary>
public static class CollectorSelfMetrics
{
    /// <summary>
    /// The source's clock minus this process's clock, in seconds, once a cycle.
    /// </summary>
    /// <remarks>
    /// One series, on the source's own entity (for vSphere, the vCenter), so
    /// whatever reports collector health reads it from one place. Positive
    /// means the source is ahead. Sample times are the source's, so this is
    /// how far "now" on a chart and "now" in the product can disagree.
    /// </remarks>
    public const string ClockSkewCounter = "collector.clockSkew.latest";
}

/// <summary>The arithmetic of the gap record, apart from where it is kept.</summary>
public static class CollectionGaps
{
    /// <summary>How much of a gap one fill query asks for.</summary>
    /// <remarks>
    /// Ten minutes: thirty real-time samples per entity, well inside what the
    /// live measurement showed one query carries safely (180 samples × 12 VMs ×
    /// 17 counters, ~2.6 MiB, ~2.5 s — docs/measurements/queryperf-sample-window.md
    /// m2). Both ends are always given; <c>maxSample</c> is never relied on,
    /// because with a window it keeps the newest samples and drops the oldest
    /// (m1) — the opposite of what a fill needs.
    /// </remarks>
    public static readonly TimeSpan SliceWidth = TimeSpan.FromMinutes(10);

    /// <summary>The gap a live read starting at <paramref name="liveStart"/> leaves behind a mark.</summary>
    /// <remarks>
    /// No mark, no gap: a fresh installation has no history to have a hole in.
    /// A mark the live read reaches is not a gap either — the live read's own
    /// few samples back absorb it.
    /// </remarks>
    public static (DateTimeOffset From, DateTimeOffset To)? ToOpen(
        DateTimeOffset? sourceMark, DateTimeOffset liveStart) =>
        sourceMark is { } mark && mark < liveStart ? (mark, liveStart) : null;

    /// <summary>The next slice to read: (start exclusive, end inclusive).</summary>
    public static (DateTimeOffset StartExclusive, DateTimeOffset EndInclusive) NextSlice(CollectionGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);

        var end = gap.FilledToUtc + SliceWidth;
        return (gap.FilledToUtc, end < gap.ToUtc ? end : gap.ToUtc);
    }

    /// <summary>The gap once everything up to <paramref name="filledTo"/> has been read again.</summary>
    /// <remarks>
    /// Never backwards and never past the end. Reaching the end closes it —
    /// as <see cref="CollectionGapState.Unrecoverable"/> if any of it had
    /// already been given up, so the record still says the history has a hole.
    /// </remarks>
    public static CollectionGap Advance(CollectionGap gap, DateTimeOffset filledTo, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(gap);

        if (gap.State != CollectionGapState.Open || filledTo <= gap.FilledToUtc)
        {
            return gap;
        }

        var to = filledTo < gap.ToUtc ? filledTo : gap.ToUtc;

        if (to < gap.ToUtc)
        {
            return gap with { FilledToUtc = to };
        }

        return gap with
        {
            FilledToUtc = to,
            State = gap.LostBeforeUtc is null ? CollectionGapState.Filled : CollectionGapState.Unrecoverable,
            ClosedAtUtc = nowUtc,
        };
    }

    /// <summary>The gap once what the platform no longer keeps has been given up.</summary>
    /// <param name="gap">The gap.</param>
    /// <param name="recoverableAfter">
    /// Samples at or before this are gone from the platform — for real-time
    /// data, the host's retention behind the server's clock.
    /// </param>
    /// <param name="nowUtc">When, for the close time.</param>
    /// <remarks>
    /// Recorded, never deleted: <see cref="CollectionGap.LostBeforeUtc"/> moves
    /// to the horizon and the fill skips to it.
    /// </remarks>
    public static CollectionGap Expire(
        CollectionGap gap, DateTimeOffset recoverableAfter, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(gap);

        if (gap.State != CollectionGapState.Open || gap.FilledToUtc >= recoverableAfter)
        {
            return gap;
        }

        var lost = recoverableAfter < gap.ToUtc ? recoverableAfter : gap.ToUtc;

        return Advance(gap with { LostBeforeUtc = lost }, lost, nowUtc);
    }
}
