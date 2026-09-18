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

    Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Supplies metric samples.</summary>
public interface IObservationSource
{
    string InstanceId { get; }

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
}

/// <summary>Metric samples from one source for one cycle.</summary>
public sealed record ObservationBatch
{
    public required string SourceInstanceId { get; init; }

    public required DateTimeOffset ReadAtUtc { get; init; }

    public IReadOnlyList<Observation> Observations { get; init; } = [];

    public IReadOnlyList<CollectionFailure> Failures { get; init; } = [];
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

    public required HealthState Health { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    public int ConsecutiveFailures { get; init; }

    /// <summary>Whether the circuit breaker is currently holding off.</summary>
    public bool IsBackingOff { get; init; }

    public string? LastFailureDetail { get; init; }
}
