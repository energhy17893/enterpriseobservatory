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

/// <summary>Supplies metric samples.</summary>
public interface IObservationSource
{
    string InstanceId { get; }

    /// <summary>Reads one batch. Not re-entrant.</summary>
    /// <remarks>
    /// <para>
    /// One instance serves every cycle — the registry hands back the same
    /// object and the host holds it as a singleton — so implementations keep
    /// state between reads: connections, sessions, and caches of what the
    /// platform said it could supply. None of that is guarded, and guarding it
    /// would not make two overlapping reads of one vCenter correct anyway;
    /// they would still compete for one session.
    /// </para>
    /// <para>
    /// So a caller must have at most one read in flight per instance.
    /// <c>SourceRunner</c> honours that within a cycle, including across its
    /// own retries: a read it abandoned at the timeout is still running inside
    /// the source, so it is not started again.
    /// </para>
    /// <para>
    /// It cannot honour it <em>between</em> cycles. A read abandoned in cycle N
    /// is still there when cycle N+1 starts thirty seconds later, and nothing
    /// can stop it — a .NET task cannot be aborted. An implementation that
    /// keeps state across reads must therefore still be safe against that one
    /// overlap; what this contract buys it is that the overlap is rare and
    /// bounded, not that it never happens.
    /// </para>
    /// </remarks>
    Task<ObservationBatch> ReadAsync(CancellationToken cancellationToken);
}

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
            or CollectionFailureKind.NotConfigured);
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
}

/// <summary>Metric samples from one source for one cycle.</summary>
public sealed record ObservationBatch
{
    public required string SourceInstanceId { get; init; }

    public required DateTimeOffset ReadAtUtc { get; init; }

    public IReadOnlyList<Observation> Observations { get; init; } = [];

    public IReadOnlyList<CollectionFailure> Failures { get; init; } = [];
}

/// <summary>Which of a source's two jobs a health record describes.</summary>
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
}

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
}
