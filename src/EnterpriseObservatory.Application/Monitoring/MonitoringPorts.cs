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
    /// Reconciles one scope: reads its state, hands it to
    /// <paramref name="reconcile"/>, and stores what comes back — all without
    /// releasing its hold on that state in between.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shaped as a callback rather than a read and a write because an operator
    /// acknowledging an alert in the gap between the two would be silently
    /// overwritten by the cycle's result: the button would appear to work and
    /// then the alert would reopen. Holding alert state across the whole
    /// decision makes a cycle and an operator's change serialise, so whichever
    /// goes second wins and neither is lost. The window is short and the loss
    /// is invisible, which is exactly the kind of defect worth designing out
    /// rather than hoping about.
    /// </para>
    /// <para>
    /// The whole result is stored rather than a list of instances, because
    /// forgetting to apply the retirements would leave resolved alerts
    /// accumulating forever, and nothing about the remaining state would look
    /// wrong.
    /// </para>
    /// <para>
    /// The callback must be pure and quick. It runs while alert state is held,
    /// and <see cref="AlertReconciler"/> is both.
    /// </para>
    /// </remarks>
    AlertReconciliationResult Reconcile(
        string scope,
        Func<IReadOnlyList<AlertInstance>, IReadOnlyList<FlapHistory>, AlertReconciliationResult> reconcile);

    /// <summary>
    /// Applies an operator's change to one alert, atomically.
    /// </summary>
    /// <param name="fingerprint">Which alert.</param>
    /// <param name="change">
    /// The lifecycle transition to apply. Runs against the instance as stored,
    /// never against a copy the caller brought with it — a client acting on
    /// what it last saw would otherwise undo whatever happened since.
    /// </param>
    /// <returns>The resulting instance, or null when there is no such alert.</returns>
    AlertInstance? Mutate(AlertFingerprint fingerprint, Func<AlertInstance, AlertInstance> change);

    /// <summary>
    /// Applies one change to many alerts, all under the same hold.
    /// </summary>
    /// <param name="fingerprints">Which alerts, in the order given.</param>
    /// <param name="change">The transition to apply to each.</param>
    /// <returns>
    /// The resulting instance per fingerprint, omitting any that were not
    /// found. A caller acting on a list that is thirty seconds old will hit
    /// some that have resolved, and that is not an error.
    /// </returns>
    /// <remarks>
    /// Not a loop over <see cref="Mutate"/>. A collection cycle could land
    /// between two of those calls, and the operator would be left with twenty
    /// alerts of which eleven are acknowledged — with nothing on the screen to
    /// say which or why.
    /// </remarks>
    IReadOnlyList<AlertInstance> MutateMany(
        IReadOnlyList<AlertFingerprint> fingerprints, Func<AlertInstance, AlertInstance> change);

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

    public EntityRetentionPolicy EntityRetention { get; init; } = EntityRetentionPolicy.Default;

    /// <summary>How often measurements are folded down and swept.</summary>
    /// <remarks>
    /// The shortest useful cadence is the finest bucket it produces: running
    /// more often than that finds no complete bucket to fold.
    /// </remarks>
    public TimeSpan CompactionInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long measurements are kept at each resolution.</summary>
    public SeriesRetentionPolicy Retention { get; init; } = SeriesRetentionPolicy.Default;

    public static MonitoringOptions Default { get; } = new();
}
