using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>Time, as a dependency, so that behaviour can be tested at any instant.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Supplies entities, relationships and configuration state.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="IObservationSource"/>. Inventory is
/// <em>state</em>: it only needs reporting when it changes. Metrics are a
/// <em>time series</em>: a new sample exists every interval whether anything
/// changed or not.
/// </para>
/// <para>
/// In the previous product both ran on the same 30-second loop, so a 2000-VM
/// estate was re-read in full every cycle while a host disconnection could
/// still take 30 seconds to notice. Keeping them apart lets each be tuned for
/// what it actually is, and lets a change-feed implementation replace the
/// polling one without the application knowing. See ADR-0005.
/// </para>
/// </remarks>
public interface IInventorySource
{
    /// <summary>Identifies this source instance, e.g. a particular vCenter.</summary>
    string InstanceId { get; }

    /// <inheritdoc cref="IObservationSource.ReadAsync"/>
    Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A server-side object a collector created — a view, a child property
/// collector, a paging token — to be given back once the read that made it is
/// over, however it ended.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0025 §3: the collector creates these and registers them; giving them
/// back — with a fresh, bounded token, never the read's own, which may already
/// be cancelled — is not the collector's decision to skip. What the object
/// actually is (a vim25 <c>ContainerView</c>, an <c>EventHistoryCollector</c>,
/// a <c>RetrievePropertiesEx</c> token) stays inside the collector that made
/// it (#80 §4); this is the only shape the rest of the product needs to know.
/// </para>
/// <para>
/// Never throws. The caller that disposes a set of these cannot let one
/// failure stop it from asking for the rest back, and a session ending is the
/// documented last resort if this could not reach the server at all.
/// </para>
/// </remarks>
public interface IServerHandle
{
    ValueTask DisposeAsync(CancellationToken freshToken);
}

/// <summary>Supplies metric samples.</summary>
public interface IObservationSource
{
    string InstanceId { get; }

    /// <summary>Reads one batch. Not re-entrant, and never asked to be.</summary>
    /// <remarks>
    /// <para>
    /// One instance serves every cycle, but it keeps no state of its own
    /// between reads any more (F5, ADR-0025 §4): what it learns — sizes the
    /// platform accepts, where each series' history stops, what the platform
    /// said it could supply — lives in <see cref="ObservationReadContext.State"/>,
    /// which the runner holds and hands to each read.
    /// </para>
    /// <para>
    /// The runner calls this at most once at a time per source, including
    /// across cycles: a read abandoned at its timeout keeps the source's slot
    /// until it truly finishes, and every cycle due meanwhile is skipped and
    /// counted (F2). That is what lets the state be unguarded.
    /// </para>
    /// <para>
    /// The read never touches the product's store (ADR-0005 §3). What it wants
    /// to happen once its samples are kept comes back on the batch as data —
    /// <see cref="ObservationBatch.Advance"/>, <see cref="ObservationBatch.GapToOpen"/>,
    /// <see cref="ObservationBatch.GapProgress"/> — and the runner does it.
    /// </para>
    /// </remarks>
    Task<ObservationBatch> ReadAsync(ObservationReadContext context, CancellationToken cancellationToken);

    /// <summary>
    /// The entities this source keeps a high-water mark for, so the runner can
    /// seed the marks from the store before the first read.
    /// </summary>
    /// <remarks>
    /// Answered from memory — what the source has been told to sample — never
    /// by asking the platform. Empty for a source that keeps no marks.
    /// </remarks>
    IReadOnlyCollection<EntityId> EntitiesWithMarks() => [];

    /// <summary>
    /// Sessions this source's own channel believes it holds, for the
    /// <c>sessionsHeld</c> self-metric (F6, ADR-0025 §5). Null for a source
    /// with no session concept.
    /// </summary>
    /// <remarks>
    /// "Believes", not "the platform confirms": a read-only account often
    /// cannot list a platform's sessions (measured for vSphere: NoPermission),
    /// so this can never detect a session the channel itself lost track of.
    /// It answers "how many did we open and not yet close", nothing more.
    /// </remarks>
    int? SessionsHeld => null;

    /// <summary>
    /// The platform's own clock, for ending read windows
    /// (<see cref="ObservationReadContext.ServerNowUtc"/>) and for the
    /// <c>clockSkew</c> self-metric (F6, ADR-0025 §5). Null when this source
    /// cannot say — the runner then uses its own clock for windowing and
    /// reports no skew.
    /// </summary>
    /// <remarks>
    /// Called once per read, by the runner, before <see cref="ReadAsync"/> —
    /// never by the source itself: this used to be the observation source's
    /// own first call (F note, VsphereObservationSource.cs:187 pre-F6), which
    /// made the server's clock a piece of business logic rather than a
    /// self-metric the runner could report even when the read that follows it
    /// fails outright.
    /// </remarks>
    Task<DateTimeOffset?> GetServerTimeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<DateTimeOffset?>(null);
}

/// <summary>
/// A stand-in for a role its connection's kind does not have — SimpliVity has
/// inventory and no metrics.
/// </summary>
/// <remarks>
/// It still fails every read as <see cref="CollectionFailureKind.NotConfigured"/>,
/// so its health row stays NotPolled and says why; but no "Collector
/// unreachable" alert is raised for it. There is nothing to reach and nothing
/// to fix: unknown is not an alarm (ADR-0026).
/// </remarks>
public interface IRoleNotApplicable;

/// <summary>Why part of a collection did not succeed.</summary>
public enum CollectionFailureKind
{
    Unreachable,
    AuthenticationRejected,
    AuthorizationDenied,
    Timeout,
    ProtocolError,
    /// <summary>The platform can supply this, but is not configured to. See the vSphere statistics level.</summary>
    InsufficientDetailLevel,
    NotConfigured,

    /// <summary>
    /// The connection has no usable password — never entered, or its stored
    /// one cannot be decrypted (ADR-0015). Distinct from
    /// <see cref="NotConfigured"/> (N1): that one is the estate honestly
    /// saying "not polled", never a reason to raise "Collector unreachable"
    /// (unknown is not an alarm, ADR-0026); a missing credential is an
    /// operator's own fix waiting, and stays an alarm until they make it.
    /// </summary>
    CredentialsUnavailable,
}

/// <summary>Whether asking again could plausibly give a different answer.</summary>
/// <remarks>
/// <para>
/// Not a cosmetic distinction. A timeout may clear on its own; a rejected
/// password will not, and every retry of one is another failed login against
/// the directory. vSphere SSO locks an account after a handful of those, so
/// retrying a credential failure is how a monitoring tool takes production
/// down — exactly what product principle 5 forbids.
/// </para>
/// <para>
/// This was discovered live rather than reasoned about: the runner retried a
/// rejected password three times a cycle, every cycle, while the message it
/// showed the operator said it did not.
/// </para>
/// </remarks>
public static class CollectionFailures
{
    /// <summary>Whether a failure of this kind should be attempted again.</summary>
    public static bool IsWorthRetrying(CollectionFailureKind kind) =>
        kind is not (CollectionFailureKind.AuthenticationRejected
            or CollectionFailureKind.AuthorizationDenied
            or CollectionFailureKind.NotConfigured
            or CollectionFailureKind.CredentialsUnavailable);
}

/// <summary>An exception that already knows why collection failed.</summary>
/// <remarks>
/// A vendor client is the only thing that can tell "wrong password" from
/// "connection reset", and the runner must not retry the first. Declared here
/// so an adapter can answer that question without the application layer
/// learning anything about the vendor.
/// </remarks>
public interface ICollectionFault
{
    /// <summary>What the source says went wrong.</summary>
    CollectionFailureKind Kind { get; }
}

/// <summary>One thing that could not be collected, and why.</summary>
/// <remarks>
/// Failures are data, not exceptions. A collector that reached seventeen of
/// twenty iLOs has neither succeeded nor failed, and forcing that into a
/// boolean loses the only information the operator needs.
/// </remarks>
public sealed record CollectionFailure
{
    public required CollectionFailureKind Kind { get; init; }

    /// <summary>What could not be collected, in the operator's terms.</summary>
    public required string Target { get; init; }

    public required string Detail { get; init; }

    /// <summary>The entity affected, when the failure maps to one.</summary>
    public EntityId? Entity { get; init; }
}

/// <summary>
/// One thing a reachable source could not read, kept with its health.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="CollectionFailure"/>. That one carries an
/// <see cref="EntityId"/> and belongs to a single collection pass; this is the
/// durable summary an operator reads days later, and keeping an entity
/// reference in it would mean a health record pointing at something the
/// retention sweep has since removed.
/// </remarks>
public sealed record PartialFailure
{
    public required CollectionFailureKind Kind { get; init; }

    /// <summary>
    /// What could not be read — a counter name, a device, an endpoint.
    /// </summary>
    /// <remarks>
    /// The actionable half. "A counter is not defined on this vCenter" sends
    /// nobody anywhere; naming the counter turns it into a decision about
    /// statistics levels or product versions.
    /// </remarks>
    public required string Target { get; init; }

    public required string Detail { get; init; }
}

/// <summary>Entities and relationships as one source currently sees them.</summary>
public sealed record InventorySnapshot
{
    public required string SourceInstanceId { get; init; }

    public required DateTimeOffset ReadAtUtc { get; init; }

    public IReadOnlyList<Entity> Entities { get; init; } = [];

    public IReadOnlyList<Relationship> Relationships { get; init; } = [];

    /// <summary>
    /// Settings this source adds to entities another source owns (ADR-0027).
    /// </summary>
    /// <remarks>
    /// Never an entity: a source that sees the same host another source
    /// already reports annotates it rather than opening a second one. See
    /// <see cref="EntityAnnotation"/>.
    /// </remarks>
    public IReadOnlyList<EntityAnnotation> Annotations { get; init; } = [];

    /// <summary>
    /// What this source could not read. Anything named here is
    /// <see cref="HealthState.Unknown"/>, never healthy.
    /// </summary>
    public IReadOnlyList<CollectionFailure> Failures { get; init; } = [];

    /// <summary>Problems the source itself reported, before any rule engine runs.</summary>
    public IReadOnlyList<AlertDefinition> Alerts { get; init; } = [];

    /// <summary>
    /// What this source managed to read, property by property.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Failures"/>, and the difference is the whole
    /// point. A failure is something that went wrong loudly enough to be
    /// named; coverage is the quiet half — a property that simply was not in
    /// the reply, which no error reports and which every rule reading it
    /// answers by staying silent. Empty for a source that does not measure
    /// its own coverage, and empty is not a claim that coverage was complete.
    /// </remarks>
    public IReadOnlyList<PropertyCoverage> Coverage { get; init; } = [];

    /// <summary>
    /// The snapshots behind each "snapshot left behind" alert in
    /// <see cref="Alerts"/>, so the pipeline can name who took them.
    /// </summary>
    /// <remarks>See <see cref="SnapshotCreators"/>.</remarks>
    public IReadOnlyList<SnapshotFinding> SnapshotFindings { get; init; } = [];

    /// <summary>
    /// Measurements the inventory read carried anyway, to be kept as series.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a second metric path. <see cref="ObservationBatch"/> is still where
    /// performance counters go; this is for the handful of figures that are
    /// inventory <em>properties</em> — a datastore's capacity and free space —
    /// and so arrive with every inventory read whether or not anybody keeps
    /// them. They were read, used for one alert and dropped, which left the
    /// product able to say "95% full" and unable to say "and it was 80% last
    /// month". See <see cref="CapacityCounters"/>.
    /// </para>
    /// <para>
    /// Stored through the same observation store under the same series
    /// identity, so a reader cannot tell which cycle wrote a series and does
    /// not need to. The resolution is the inventory interval.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Observation> Observations { get; init; } = [];

    /// <summary>
    /// How many server-side views this read's own session held when it
    /// finished asking for them back — the <c>views_held</c> self-metric
    /// (F4, ADR-0025 §3). Zero for a source that does not create views; null
    /// when a source that does create them could not read the count. A null
    /// reading must not be mistaken for zero — see
    /// <see cref="CollectorHealth.ViewsHeldMax"/>'s remarks.
    /// </summary>
    public int? ViewsHeld { get; init; }
}

/// <summary>Metric samples from one source for one cycle.</summary>
public sealed record ObservationBatch
{
    public required string SourceInstanceId { get; init; }

    public required DateTimeOffset ReadAtUtc { get; init; }

    /// <summary>The current value of each series: what the rules reason about.</summary>
    public IReadOnlyList<Observation> Observations { get; init; } = [];

    /// <summary>
    /// Earlier samples the same read carried, to be kept but not reasoned about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A platform that samples faster than it is polled hands back more than
    /// one sample per series, and keeping only the newest loses the rest for
    /// good — for a summation, the event itself. They are stored under the
    /// time the platform took them, so a sample seen by two consecutive reads
    /// is one row, not two.
    /// </para>
    /// <para>
    /// Apart from <see cref="Observations"/> because a rule handed several
    /// values for one series would have to decide which it meant, and every
    /// rule written so far means the current one. Empty for a source that has
    /// nothing earlier to offer.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Observation> Backfill { get; init; } = [];

    public IReadOnlyList<CollectionFailure> Failures { get; init; } = [];

    /// <summary>
    /// How the source's learned state moves once this batch is safely kept.
    /// </summary>
    /// <remarks>
    /// Data, applied by the runner after the store queue accepts the batch and
    /// never otherwise (see <see cref="IStateAdvance"/>). A source that keeps a
    /// high-water mark must move it only past samples that were actually kept:
    /// moved on the read, a failed write would leave the mark ahead of the
    /// history and the hole behind it would never be asked for again. Null for
    /// a source with nothing to move.
    /// </remarks>
    public IStateAdvance? Advance { get; init; }

    /// <summary>A source-level gap this read found, for the runner to record.</summary>
    /// <remarks>
    /// Recorded right after the read, whether or not the samples are kept:
    /// the gap exists either way. Its id is the store's, so it is filled from
    /// the next read on.
    /// </remarks>
    public CollectionGap? GapToOpen { get; init; }

    /// <summary>
    /// Recorded gaps this read filled or found past retention, as they now
    /// stand — written by the runner once the batch is accepted.
    /// </summary>
    public IReadOnlyList<CollectionGap> GapProgress { get; init; } = [];

    /// <summary>
    /// The stretch of the platform's history this batch's live read covered,
    /// and how long the platform still keeps it — or null for a source that
    /// cannot be asked again.
    /// </summary>
    /// <remarks>
    /// What makes a batch the store queue drops recoverable: the runner
    /// records the span as a collection gap and the source reads it again
    /// (F5). An outage shorter than about an hour loses no data; beyond it,
    /// the loss is reported.
    /// </remarks>
    public RefetchableSpan? Refetchable { get; init; }

    /// <summary>The runner slot that read this batch, which accepts it.</summary>
    internal ObservationSourceSlot? Slot { get; init; }

    /// <summary>
    /// Sessions this source's own channel believes it holds (F6). Filled by
    /// <see cref="ObservationSourceSlot"/> from <see cref="IObservationSource.SessionsHeld"/>
    /// before the batch is returned — never by the collector's own read.
    /// </summary>
    public int? SessionsHeld { get; init; }

    /// <summary>
    /// The source's clock minus this process's clock, in seconds (F6). Filled
    /// by <see cref="ObservationSourceSlot"/> from the same
    /// <see cref="IObservationSource.GetServerTimeAsync"/> call that supplied
    /// <see cref="ObservationReadContext.ServerNowUtc"/> — never an
    /// <see cref="Observation"/> the collector adds to <see cref="Backfill"/>.
    /// </summary>
    public double? ClockSkewSeconds { get; init; }
}

/// <summary>
/// A stretch of a source's history the platform can be asked for again, and
/// until when.
/// </summary>
/// <remarks>
/// <see cref="FromExclusiveUtc"/> and <see cref="ToInclusiveUtc"/> are the
/// platform's times, the same convention as <see cref="CollectionGap"/>;
/// <see cref="RecoverableUntilUtc"/> is this process's clock — the moment the
/// newest of it leaves what the platform keeps, less a margin.
/// </remarks>
public sealed record RefetchableSpan
{
    public required DateTimeOffset FromExclusiveUtc { get; init; }

    public required DateTimeOffset ToInclusiveUtc { get; init; }

    public required DateTimeOffset RecoverableUntilUtc { get; init; }
}

/// <summary>Which of a source's jobs a health record describes.</summary>
/// <remarks>
/// One vCenter is read by two collectors on two schedules, and they fail
/// independently: an account may be able to list inventory while the statistics
/// level denies it metrics. Tracking one health record per address would let
/// the thirty-second cycle's successes keep resetting the five-minute cycle's
/// failure count, so a persistently broken inventory read would never reach the
/// circuit breaker and never be reported.
/// </remarks>
public enum CollectorRole
{
    Inventory,
    Observation,

    /// <summary>
    /// The event stream (F1). Its own record for the same reason: a vCenter
    /// whose event manager keeps failing while inventory answers is backed off
    /// from, without inventory's successes resetting the count. Stored as
    /// text, so no migration.
    /// </summary>
    Events,
}

/// <summary>
/// The self-metrics one result can answer for itself, beyond duration and the
/// failure count every result already carries (F6, ADR-0025 §5).
/// </summary>
/// <remarks>
/// One shape for every role, read from the result by the pipeline that knows
/// what "read" means for it — <see cref="ObservationBatch.Observations"/> for
/// metrics, <see cref="InventorySnapshot.Entities"/> for inventory. Every
/// field is optional because not every role or every source can answer every
/// one of them, and "does not apply" must never collapse into zero.
/// </remarks>
public readonly record struct SelfMetricsExtras(
    int? ItemsRead = null,
    int? ViewsHeld = null,
    int? SessionsHeld = null,
    double? ClockSkewSeconds = null);

/// <summary>How a source is behaving over time.</summary>
/// <remarks>
/// Collector health is observable state rather than a log line, so the question
/// "is the monitoring system itself working" can be answered from inside the
/// product. See ADR-0005.
/// </remarks>
public sealed record CollectorHealth
{
    public required string InstanceId { get; init; }

    /// <summary>Which job this describes. See <see cref="CollectorRole"/>.</summary>
    public required CollectorRole Role { get; init; }

    public required HealthState Health { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    public int ConsecutiveFailures { get; init; }

    /// <summary>Whether the circuit breaker is currently holding off.</summary>
    public bool IsBackingOff { get; init; }

    /// <summary>
    /// Why the last attempt failed outright, when it did.
    /// </summary>
    /// <remarks>
    /// Reaching a source and failing to read all of it is a different thing
    /// from not reaching it, and this used to carry both. One string cannot,
    /// so the two were indistinguishable on screen: a collector that could not
    /// authenticate and a collector missing one counter read the same.
    /// See <see cref="PartialFailures"/>.
    /// </remarks>
    public string? LastFailureDetail { get; init; }

    /// <summary>
    /// What the source could not read on the last attempt that otherwise worked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All of them, which is the point. This was a single string holding
    /// <c>failures[0]</c>, so a collector with three unrelated problems showed
    /// one and silently dropped the rest — and the dropped ones were invisible
    /// everywhere, including the API. Against a real estate that hid an entire
    /// class of measurement behind an unrelated message about a different
    /// entity type.
    /// </para>
    /// <para>
    /// Anything named here is <see cref="HealthState.Unknown"/>, never healthy.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PartialFailure> PartialFailures { get; init; } = [];

    /// <summary>When the source was last actually asked, successfully or not.</summary>
    /// <remarks>
    /// The breaker's cooldown is measured from here, not from the last success.
    /// Measuring from the last success means a source that has been down longer
    /// than the cooldown is never held off at all — the opposite of what a
    /// breaker is for, and the reason a wrong password was retried every cycle.
    /// </remarks>
    public DateTimeOffset? LastAttemptUtc { get; init; }

    /// <summary>Why the last attempt failed, when the source said why.</summary>
    /// <remarks>
    /// Kept because the answer changes what the breaker does: a kind that is
    /// not worth retrying holds the source off after a single failure rather
    /// than after five.
    /// </remarks>
    public CollectionFailureKind? LastFailureKind { get; init; }

    /// <summary>
    /// Cycles skipped because a previous read of this source was still running
    /// when the next one was due.
    /// </summary>
    /// <remarks>
    /// F2 (F note §8 decision 1), Telegraf's rule: a source that has not
    /// finished by the time its next cycle is due is not asked again on top of
    /// itself — the abandoned or overrunning read is left alone, and the whole
    /// cycle is skipped rather than queued behind it. Counted here, cumulative
    /// for the life of the process, so "we are not looking this cycle" stays
    /// visible next to the rest of a source's collection numbers rather than
    /// only in a log line.
    /// </remarks>
    public int SkippedCycles { get; init; }

    /// <summary>
    /// The server-side views this source's own session held at the end of its
    /// last cycle (F4, ADR-0025 §3). Zero for a role that does not create
    /// views. Not an alert either way — package D's proof, not a threshold.
    /// </summary>
    /// <remarks>
    /// Null when the last cycle could not read the count at all — never
    /// invented as zero. An unreadable list and an empty list are different
    /// facts: this product's rule everywhere else is that not being allowed
    /// to look is not an empty list, and collapsing the two here would let a
    /// self-metric that never actually measured anything read as a clean
    /// zero.
    /// </remarks>
    public int? ViewsHeld { get; init; }

    /// <summary>
    /// The highest <see cref="ViewsHeld"/> this process has seen since it
    /// started.
    /// </summary>
    /// <remarks>
    /// Deliberately not carried forward from what was persisted: it answers
    /// "has cleanup regressed since this process came up", so a restart resets
    /// it to null rather than inheriting a number an earlier build left
    /// behind. The store that persists this (<c>PostgresCollectorHealthStore</c>)
    /// never hydrates either this or <see cref="ViewsHeld"/> from the database
    /// on startup for that reason; both simply start null and are written
    /// fresh every cycle. It is still persisted every cycle, so a query an
    /// hour or a day later can prove "never above zero since restart" without
    /// this process having to be watched the whole time.
    /// <para>
    /// Null here means "never read successfully since this process started" —
    /// not zero, and not carried down from an unreadable reading either: a
    /// null <see cref="ViewsHeld"/> reading leaves this exactly where it was.
    /// A monitoring tool that reported "0 views held, ever" while every read
    /// had actually failed would be proving the wrong thing.
    /// </para>
    /// </remarks>
    public int? ViewsHeldMax { get; init; }

    // --- F6: self-metrics the runner produces itself, never the collector ---

    /// <summary>How long the last attempt took, start to finish (success or failure).</summary>
    public TimeSpan? LastDuration { get; init; }

    /// <summary>
    /// The last <see cref="RecentDurationsCapacity"/> attempts' durations,
    /// oldest first — successful or not, since a source timing out is itself
    /// a duration worth trending.
    /// </summary>
    /// <remarks>
    /// A ring, not one "last duration": Datadog's check <c>Stats</c> keeps the
    /// last 32 for the same reason (§10.6) — a single figure cannot show a
    /// trend, and a trend is what tells "one slow cycle" from "getting slower".
    /// </remarks>
    public IReadOnlyList<TimeSpan> RecentDurations { get; init; } = [];

    public const int RecentDurationsCapacity = 32;

    /// <summary>Items this attempt read — samples for observation, entities for inventory, events for events.</summary>
    public int? ItemsRead { get; init; }

    /// <summary>
    /// Items this attempt could not read — the count behind <see cref="PartialFailures"/>.
    /// </summary>
    public int? ItemsUnread { get; init; }

    /// <summary>
    /// Sessions this source's own channel believes it holds. Named
    /// deliberately: a read-only account often cannot list a platform's own
    /// sessions (measured for vSphere: <c>NoPermission</c>), so this can never
    /// confirm or detect a session the channel itself lost track of — it
    /// answers only "how many did we open and not yet close".
    /// </summary>
    public int? SessionsHeld { get; init; }

    /// <summary>The source's clock minus this process's clock, in seconds, from the last successful probe.</summary>
    /// <remarks>Positive means the source is ahead. See <see cref="CollectorSelfMetrics.ClockSkewCounter"/>.</remarks>
    public double? ClockSkewSeconds { get; init; }

    /// <summary>Attempts made since this process started, successful or not.</summary>
    public long TotalAttempts { get; init; }

    /// <summary>Attempts that failed outright since this process started (not counting partial failures).</summary>
    public long TotalFailures { get; init; }

    /// <summary>
    /// Whether the last attempt is healthy, in Prometheus's exact sense of
    /// <c>up</c>: the scrape <em>succeeded</em> — the source answered, and the
    /// reply was parsed and kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// False on: no answer at all, a fault the source itself says will not
    /// clear (<see cref="LastFailureKind"/> set), a timeout or an abandoned
    /// read, a reply that could not be parsed, or bookkeeping the read
    /// depended on (the gap record, the store queue) refusing to write.
    /// </para>
    /// <para>
    /// True for a complete read that carries only <em>per-item</em> partial
    /// failures the estate itself explains — <see cref="CollectionFailureKind.NotConfigured"/>
    /// (a feature this platform simply is not configured for) and
    /// <see cref="CollectionFailureKind.InsufficientDetailLevel"/> (the
    /// statistics level is too low). Those are environment conditions, not a
    /// failed scrape, and are already visible through
    /// <see cref="ItemsUnread"/> and <see cref="PartialFailures"/> — a
    /// datastore whose latency counter Storage I/O Control has switched off
    /// estate-wide must not turn <c>up</c> false forever on an otherwise
    /// healthy vCenter. Deliberately not tied to
    /// <see cref="HealthState.Warning"/>: that rollup already conflates the
    /// two cases this field exists to separate.
    /// </para>
    /// <para>
    /// Set explicitly by <see cref="SourceRunner"/> from the attempt's own
    /// outcome, not derived from <see cref="Health"/>.
    /// </para>
    /// </remarks>
    public bool Up { get; init; }
}
