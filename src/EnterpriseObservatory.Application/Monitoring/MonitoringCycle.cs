using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Monitoring;

/// <summary>What one pass over the estate produced.</summary>
public sealed record MonitoringCycleResult
{
    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>Alerts an operator should see right now.</summary>
    public IReadOnlyList<AlertInstance> Visible { get; init; } = [];

    /// <summary>Alerts owing a notification that is not suppressed.</summary>
    public IReadOnlyList<AlertInstance> ToNotify { get; init; } = [];

    /// <summary>Samples gathered this cycle.</summary>
    public IReadOnlyList<Observation> Observations { get; init; } = [];

    /// <summary>
    /// Why the samples could not be recorded, if they could not.
    /// </summary>
    /// <remarks>
    /// Collecting and keeping are different things, and a product that does the
    /// first but not the second looks healthy right up until somebody asks what
    /// happened yesterday.
    /// </remarks>
    public string? StorageFailure { get; init; }

    public int ActiveEntities { get; init; }

    public int VanishedEntities { get; init; }

    /// <summary>
    /// Sources that did not answer this cycle.
    /// </summary>
    /// <remarks>
    /// Reported separately from alerts because it changes what the rest of the
    /// result means: everything these sources cover is unknown, not healthy.
    /// </remarks>
    public IReadOnlyList<string> SilentSources { get; init; } = [];
}

/// <summary>
/// Runs one pass: read, resolve identity, evaluate, persist.
/// </summary>
/// <remarks>
/// <para>
/// The composition of everything below it, and the only place the order of
/// those steps is decided. It holds no state itself, which is what lets the
/// same cycle run in a single process today and across two tomorrow.
/// </para>
/// <para>
/// Inventory and observation are separate methods rather than one, because they
/// run on different schedules. See ADR-0005.
/// </para>
/// </remarks>
public sealed class MonitoringCycle(
    InventoryCollectionPipeline inventoryPipeline,
    ObservationCollectionPipeline observationPipeline,
    IEntityGraphStore graphStore,
    IAlertStateStore alertStore,
    ICollectorHealthStore healthStore,
    IAlertNotifier notifier,
    IObservationStore observations,
    IClock clock)
{
    private readonly InventoryCollectionPipeline _inventory =
        inventoryPipeline ?? throw new ArgumentNullException(nameof(inventoryPipeline));

    private readonly ObservationCollectionPipeline _observations =
        observationPipeline ?? throw new ArgumentNullException(nameof(observationPipeline));

    private readonly IEntityGraphStore _graphStore =
        graphStore ?? throw new ArgumentNullException(nameof(graphStore));

    private readonly IAlertStateStore _alertStore =
        alertStore ?? throw new ArgumentNullException(nameof(alertStore));

    private readonly ICollectorHealthStore _healthStore =
        healthStore ?? throw new ArgumentNullException(nameof(healthStore));

    private readonly IAlertNotifier _notifier =
        notifier ?? throw new ArgumentNullException(nameof(notifier));

    private readonly IObservationStore _observationStore =
        observations ?? throw new ArgumentNullException(nameof(observations));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Re-reads inventory and folds it into the graph.</summary>
    public async Task<MonitoringCycleResult> RunInventoryAsync(
        IReadOnlyList<IInventorySource> sources,
        MonitoringOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        var now = _clock.UtcNow;

        var cycle = await _inventory
            .RunAsync(sources, _healthStore.Current, options.Collection, cancellationToken)
            .ConfigureAwait(false);

        _healthStore.Merge(cycle.Health);

        // Only sources that actually returned a snapshot may cause their own
        // entities to be treated as vanished. A source we could not reach has
        // told us nothing about what still exists. See EntityGraph.Merge.
        var reporting = cycle.Snapshots.Select(s => s.SourceInstanceId).ToList();
        var silent = sources
            .Select(s => s.InstanceId)
            .Where(id => !reporting.Contains(id, StringComparer.Ordinal))
            .ToList();

        var entities = cycle.Snapshots.SelectMany(s => s.Entities).ToList();
        var relationships = cycle.Snapshots.SelectMany(s => s.Relationships).ToList();

        var graph = _graphStore.Current.Merge(
            entities, relationships, reporting, now, options.EntityRetention);

        graph = graph with { Relationships = [.. graph.Relationships, .. ResolveIdentity(graph, now)] };

        _graphStore.Replace(graph);

        var observed = cycle.Snapshots.SelectMany(s => s.Alerts)
            .Concat(cycle.CollectionAlerts)
            .ToList();

        var reconciliation = Reconcile(AlertScopes.Inventory, observed, options, now);

        await NotifyAsync(AlertScopes.Inventory, reconciliation, cancellationToken)
            .ConfigureAwait(false);

        return new MonitoringCycleResult
        {
            AtUtc = now,
            Visible = VisibleInbox(),
            ToNotify = reconciliation.ToNotify,
            ActiveEntities = graph.Active.Count(),
            VanishedEntities = graph.Vanished.Count(),
            SilentSources = silent,
        };
    }

    /// <summary>Samples metrics and folds the resulting alerts in.</summary>
    public async Task<MonitoringCycleResult> RunObservationsAsync(
        IReadOnlyList<IObservationSource> sources,
        MonitoringOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        var now = _clock.UtcNow;

        var cycle = await _observations
            .RunAsync(sources, _healthStore.Current, options.Collection, cancellationToken)
            .ConfigureAwait(false);

        _healthStore.Merge(cycle.Health);
        StoreObservations(cycle.Observations);

        var reconciliation = Reconcile(AlertScopes.Observation, cycle.CollectionAlerts, options, now);

        await NotifyAsync(AlertScopes.Observation, reconciliation, cancellationToken)
            .ConfigureAwait(false);

        var graph = _graphStore.Current;
        var answered = cycle.Batches.Select(b => b.SourceInstanceId).ToList();

        return new MonitoringCycleResult
        {
            AtUtc = now,
            Visible = VisibleInbox(),
            ToNotify = reconciliation.ToNotify,
            Observations = cycle.Observations,
            StorageFailure = _lastStorageFailure,
            ActiveEntities = graph.Active.Count(),
            VanishedEntities = graph.Vanished.Count(),
            SilentSources =
            [
                .. sources.Select(s => s.InstanceId)
                    .Where(id => !answered.Contains(id, StringComparer.Ordinal)),
            ],
        };
    }

    /// <summary>
    /// Dispatches this cycle's notifications and records that it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Marked only after the dispatcher returns. A pending notification that
    /// was cleared before it was sent is one nobody will ever be told about,
    /// and the alert would sit open and silent until its severity changed.
    /// Failing the other way sends a duplicate, which is a far smaller harm.
    /// </para>
    /// <para>
    /// Marking is not optional: the pending kind survives every subsequent
    /// observation, so an alert that is never marked notifies on every cycle
    /// for as long as it fires.
    /// </para>
    /// </remarks>
    private async Task NotifyAsync(
        string scope,
        AlertReconciliationResult reconciliation,
        CancellationToken cancellationToken)
    {
        if (reconciliation.ToNotify.Count == 0)
        {
            return;
        }

        await _notifier.DispatchAsync(reconciliation.ToNotify, cancellationToken).ConfigureAwait(false);

        _alertStore.MarkNotified(scope, [.. reconciliation.ToNotify.Select(a => a.Fingerprint)]);
    }

    /// <summary>
    /// Records this cycle's samples.
    /// </summary>
    /// <remarks>
    /// A storage failure must not end the cycle. Losing a sample costs one
    /// point on one chart and the next cycle replaces it; a collection loop
    /// that stopped because the disk was busy is a blind monitoring system, and
    /// that is very much worse. The failure is rethrown as nothing and instead
    /// surfaces where it belongs — the samples simply are not there, and a gap
    /// is already how this product says "we were not looking".
    /// </remarks>
    private void StoreObservations(IReadOnlyList<Observation> observations)
    {
        if (observations.Count == 0)
        {
            return;
        }

        try
        {
            _observationStore.Append(observations);
        }
#pragma warning disable CA1031 // Justified: see the remarks above.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _lastStorageFailure = ex.Message;
        }
    }

    /// <summary>Why the last attempt to record samples failed, if it did.</summary>
    /// <remarks>
    /// Reported on the cycle result rather than only logged, so that "we are
    /// collecting but not keeping" is visible in the product rather than in a
    /// file on the server. See ADR-0005.
    /// </remarks>
    private string? _lastStorageFailure;

    /// <summary>
    /// What an operator should see right now, across every scope.
    /// </summary>
    /// <remarks>
    /// Read from the store rather than from this cycle's reconciliation. A
    /// metric cycle decides nothing about inventory alerts, but an operator
    /// still has exactly one inbox and it must not appear to empty and refill
    /// as the two cycles take turns. See ADR-0007.
    /// </remarks>
    private IReadOnlyList<AlertInstance> VisibleInbox() =>
        [.. _alertStore.All.Where(i => i.IsVisible)];

    /// <summary>
    /// Advances alert state for one scope.
    /// </summary>
    /// <remarks>
    /// Reading and writing the same scope is the whole of the safety here: the
    /// reconciler resolves anything it was given but did not see, so a cycle
    /// that read the other scope's instances would clear them. See
    /// <see cref="AlertScopes"/>.
    /// </remarks>
    private AlertReconciliationResult Reconcile(
        string scope,
        IReadOnlyList<AlertDefinition> observed,
        MonitoringOptions options,
        DateTimeOffset now)
    {
        var result = AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Observed = observed,
            Stored = _alertStore.InstancesIn(scope),
            FlapHistories = _alertStore.FlapHistoriesIn(scope),
            Hysteresis = options.Hysteresis,
            Flap = options.Flap,
            NowUtc = now,
        });

        _alertStore.Apply(scope, result);
        return result;
    }

    /// <summary>
    /// Links records that describe the same real machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only entities carrying identity marks take part. Most virtual machines
    /// carry none, and comparing them would dominate the cost while producing
    /// nothing: resolution is pairwise, so halving the input quarters the work.
    /// </para>
    /// <para>
    /// It is still quadratic. At a few hundred marked entities that is
    /// irrelevant; at tens of thousands it will need an index on the marks
    /// themselves. Noted rather than solved, because solving it now would be
    /// guessing at a shape we have not measured.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<Relationship> ResolveIdentity(EntityGraph graph, DateTimeOffset now)
    {
        var candidates = graph.Active.Where(e => e.Marks.Count > 0).ToList();

        return candidates.Count < 2
            ? []
            : IdentityResolver.Resolve(candidates, now).SameAsEdges;
    }
}
