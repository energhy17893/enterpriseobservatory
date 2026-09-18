using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Monitoring;

/// <summary>
/// Holds the entity graph between cycles.
/// </summary>
/// <remarks>
/// A port rather than a field so that the same cycle code runs against memory
/// in a single process and against shared storage when the collector and the
/// web host are split apart. See ADR-0001.
/// </remarks>
public interface IEntityGraphStore
{
    EntityGraph Current { get; }

    void Replace(EntityGraph graph);
}

/// <summary>
/// Holds alert state between cycles.
/// </summary>
/// <remarks>
/// Reads and writes are per scope, because reconciliation treats its input as
/// the complete truth. Handing one cycle the other's instances would resolve
/// them for never having been observed — the metric cycle would clear every
/// inventory alert thirty seconds after it was raised. See <c>AlertScopes</c>.
/// </remarks>
public interface IAlertStateStore
{
    /// <summary>Everything currently open, across all scopes, for display.</summary>
    /// <remarks>
    /// The inbox reads this. It is deliberately not what a cycle reconciles
    /// against — see the remarks on this interface.
    /// </remarks>
    IReadOnlyList<AlertInstance> All { get; }

    IReadOnlyList<AlertInstance> InstancesIn(string scope);

    IReadOnlyList<FlapHistory> FlapHistoriesIn(string scope);

    /// <summary>
    /// Applies a reconciliation to one scope, including retiring what it
    /// retired.
    /// </summary>
    /// <remarks>
    /// Takes the whole result rather than a list of instances, because
    /// forgetting to apply the retirements would leave resolved alerts
    /// accumulating forever — and nothing about the remaining state would look
    /// wrong.
    /// </remarks>
    void Apply(string scope, AlertReconciliationResult result);

    /// <summary>
    /// Clears the pending notification on instances that have been dispatched.
    /// </summary>
    /// <remarks>
    /// A pending notification survives every subsequent observation until it is
    /// cleared, which is deliberate — a notification that failed to send must
    /// not be forgotten. The consequence is that failing to call this makes an
    /// alert notify on every cycle for as long as it fires.
    /// </remarks>
    void MarkNotified(string scope, IReadOnlyList<AlertFingerprint> fingerprints);
}

/// <summary>Holds collector health between cycles.</summary>
/// <remarks>
/// Keyed by source <em>and</em> role: one vCenter is read by two collectors
/// that fail independently. See <see cref="CollectorRole"/>.
/// </remarks>
public interface ICollectorHealthStore
{
    IReadOnlyList<CollectorHealth> Current { get; }

    void Merge(IReadOnlyList<CollectorHealth> health);
}

/// <summary>Where a cycle's notifications go.</summary>
/// <remarks>
/// The cycle does not decide channels. It reports what happened and of what
/// kind; routing "it got worse" to a pager and "it got better" to a log is the
/// dispatcher's decision. See <see cref="AlertNotificationKind"/>.
/// </remarks>
public interface IAlertNotifier
{
    Task DispatchAsync(IReadOnlyList<AlertInstance> pending, CancellationToken cancellationToken);
}

/// <summary>Configuration for one monitoring cycle.</summary>
public sealed record MonitoringOptions
{
    /// <summary>How often inventory is re-read.</summary>
    /// <remarks>
    /// Slower than metrics on purpose: inventory is state and only needs
    /// reading when it changes, while metrics are a time series. Until a
    /// change feed is in place this is a poll, so the interval is the
    /// compromise between staleness and load. See ADR-0005.
    /// </remarks>
    public TimeSpan InventoryInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often metrics are sampled.</summary>
    public TimeSpan ObservationInterval { get; init; } = TimeSpan.FromSeconds(30);

    public CollectionPolicy Collection { get; init; } = CollectionPolicy.Default;

    public HysteresisPolicy Hysteresis { get; init; } = HysteresisPolicy.Default;

    public FlapPolicy Flap { get; init; } = FlapPolicy.Default;

    public EntityRetentionPolicy Retention { get; init; } = EntityRetentionPolicy.Default;

    public static MonitoringOptions Default { get; } = new();
}
