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
    ICoverageStore coverageStore,
    IAlertNotifier notifier,
    IObservationStore observations,
    IMaintenanceWindowStore maintenance,
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

    private readonly ICoverageStore _coverageStore =
        coverageStore ?? throw new ArgumentNullException(nameof(coverageStore));

    private readonly ICollectorHealthStore _healthStore =
        healthStore ?? throw new ArgumentNullException(nameof(healthStore));

    private readonly IAlertNotifier _notifier =
        notifier ?? throw new ArgumentNullException(nameof(notifier));

    private readonly IObservationStore _observationStore =
        observations ?? throw new ArgumentNullException(nameof(observations));

    private readonly IMaintenanceWindowStore _maintenance =
        maintenance ?? throw new ArgumentNullException(nameof(maintenance));

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

        // Guarded per write, and named per cycle: the two cycles merge health
        // on different clocks and must be able to fail without resolving each
        // other's alert, for the reason SourceRunner keeps the collector's role
        // in its fingerprint.
        var healthFailure = Guarded(
            "collector-health:inventory",
            "the collectors' health",
            () => _healthStore.Merge(cycle.Health));

        // Only sources that actually returned a snapshot may cause their own
        // entities to be treated as vanished. A source we could not reach has
        // told us nothing about what still exists. See EntityGraph.Merge.
        var reporting = cycle.Snapshots.Select(s => s.SourceInstanceId).ToList();
        var silent = sources
            .Select(s => s.InstanceId)
            .Where(id => !reporting.Contains(id, StringComparer.Ordinal))
            .ToList();

        // Recorded before anything reasons about the entities, so a rule that
        // throws still leaves behind the account of what was readable. A
        // coverage report is least useful exactly when the cycle went wrong.
        // Guarded per source, and named per source: one vCenter's coverage
        // failing to write must not resolve another's alert, for the reason
        // health keeps the role in its fingerprint.
        var coverageFailures = cycle.Snapshots
            .SelectMany(snapshot => Guarded(
                $"coverage:{snapshot.SourceInstanceId}",
                $"the coverage measured by '{snapshot.SourceInstanceId}'",
                () => _coverageStore.Replace(
                    snapshot.SourceInstanceId, snapshot.Coverage, snapshot.ReadAtUtc)))
            .ToList();

        var entities = cycle.Snapshots.SelectMany(s => s.Entities).ToList();
        var relationships = cycle.Snapshots.SelectMany(s => s.Relationships).ToList();

        var graph = _graphStore.Current.Merge(
            entities, relationships, reporting, now, options.EntityRetention);

        graph = graph with { Relationships = [.. graph.Relationships, .. ResolveIdentity(graph, now)] };

        // The order is what made this one dangerous. The graph is written
        // before reconciliation, so a throw here took the cycle's collection
        // alerts with it -- "Collector unreachable" included. The product went
        // quiet about vCenters it could not reach because a different subsystem
        // could not write, which is the failure this cycle exists to report.
        var graphFailure = Guarded(
            "entity-graph", "the topology", () => _graphStore.Replace(graph));

        IReadOnlyList<AlertDefinition> observed =
        [
            .. coverageFailures,
            .. cycle.Snapshots.SelectMany(s => s.Alerts),
            .. cycle.CollectionAlerts,
            .. healthFailure,
            .. graphFailure,

            // The one rule that belongs to this cycle rather than the metric
            // one, and the scope is the argument. It judges the path table,
            // which is read on the inventory rhythm and changes on it; running
            // it beside the counters would have the faster cycle re-deciding a
            // fact nothing had re-read, and — because reconciliation treats
            // what it is given as the whole truth — each cycle resolving the
            // other's findings.
            //
            // Guarded like every other rule: a bug in counting paths must cost
            // the path count and not this cycle's "Collector unreachable".
            // Given the graph as this cycle merged it rather than the store's
            // copy, so that a failed write leaves the rule reasoning about
            // what was actually just read.
            .. Analysis.GuardedRule.Run(
                Analysis.StoragePathRedundancy.RuleId,
                () => Analysis.StoragePathRedundancy.Evaluate(
                    [.. graph.Active], options.StoragePathRedundancy)),

            // The first rule that reads configuration rather than measurement.
            // It rides the inventory rhythm because that is when the setting
            // is read: a value that changes when somebody changes it has
            // nothing to say every twenty seconds.
            .. Analysis.GuardedRule.Run(
                Analysis.RemoteLogging.RuleId,
                () => Analysis.RemoteLogging.Evaluate(
                    [.. graph.Active], options.RemoteLogging)),

            // Not a rule about the estate but a rule about this product: what
            // it managed to read. It goes last because everything above it is
            // entitled to be silent, and this is the only thing that can tell
            // an operator whether a silence was a verdict or a gap.
            .. Analysis.GuardedRule.Run(
                Analysis.CollectionCoverage.RuleId,
                () => Analysis.CollectionCoverage.Evaluate(
                    cycle.Snapshots.ToDictionary(
                        s => s.SourceInstanceId,
                        s => s.Coverage,
                        StringComparer.Ordinal))),
        ];

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

        // Every source has already been read and the samples are in memory. A
        // pool exhaustion or a statement timeout in here used to end the pass
        // before StoreObservations -- the one piece of code written to keep
        // those samples -- was ever reached.
        var healthFailure = Guarded(
            "collector-health:metrics",
            "the collectors' health",
            () => _healthStore.Merge(cycle.Health));

        StoreObservations(cycle.Observations);

        // What the collectors could not read, and what the numbers themselves
        // say. Both belong to this scope because both are decided by this
        // cycle: reconciliation treats what it is given as the whole truth, so
        // a rule evaluated here must have its alerts reconciled here or the
        // next pass would resolve them.
        // Each rule is guarded, so a bug in one costs that rule and nothing
        // else. Collection sources and storage have always been isolated this
        // way; rules were the one part of the cycle that could still take the
        // whole thing down with them.
        IReadOnlyList<AlertDefinition> observed =
        [
            .. cycle.CollectionAlerts,
            .. healthFailure,
            .. Analysis.GuardedRule.Run(
                Analysis.FaultCounters.RuleId,
                () => Analysis.FaultCounters.Evaluate(cycle.Observations)),
            .. Analysis.GuardedRule.Run(
                Analysis.PeerOutliers.RuleId,
                () => Analysis.PeerOutliers.Evaluate(cycle.Observations, options.PeerOutliers)),
            .. Analysis.GuardedRule.Run(
                Analysis.CpuContention.RuleId,
                () => Analysis.CpuContention.Evaluate(
                    cycle.Observations, _graphStore.Current, options.CpuContention)),
            .. Analysis.GuardedRule.Run(
                Analysis.StorageLayerSplit.RuleId,
                () => Analysis.StorageLayerSplit.Evaluate(
                    cycle.Observations, options.StorageLayers)),

            // Its peer policy is forced to the one PeerOutliers was given, not
            // merely defaulted to the same value. The two rules are mutually
            // exclusive by recomputing each other's test, and two copies that
            // drifted apart would open a band where both fire, or neither
            // does, with nothing to say so.
            .. Analysis.GuardedRule.Run(
                Analysis.SharedVolumeLatency.RuleId,
                () => Analysis.SharedVolumeLatency.Evaluate(
                    cycle.Observations,
                    options.SharedVolumes with { Peers = options.PeerOutliers })),
            .. Analysis.GuardedRule.Run(
                Analysis.StorageLatencyBlindSpot.RuleId,
                () => Analysis.StorageLatencyBlindSpot.Evaluate(
                    cycle.Observations, options.StorageLatencyBlindSpot)),

            // Given the graph for VM BackedBy Datastore, and the sample store
            // for the one question a single cycle cannot answer: whether the
            // volume's load rose. The store is read only for a volume that has
            // already passed every other gate.
            .. Analysis.GuardedRule.Run(
                Analysis.StorageNoisyNeighbour.RuleId,
                () => Analysis.StorageNoisyNeighbour.Evaluate(
                    cycle.Observations,
                    _graphStore.Current,
                    Analysis.StorageNoisyNeighbour.TypicalRateFrom(
                        _observationStore, now, options.StorageNoisyNeighbour),
                    options.StorageNoisyNeighbour with { Peers = options.PeerOutliers })),
        ];

        var reconciliation = Reconcile(AlertScopes.Observation, observed, options, now);

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
            // Deliberately neither set nor cleared. Nothing was written, so
            // nothing new is known about whether writing works, and clearing
            // here would report "we are keeping your samples" on the strength
            // of an attempt never made -- the same fabrication as a zero that
            // was never measured. The cost of holding it is that a cycle with
            // nothing to store repeats the last real failure; that is bounded
            // by the first cycle that has a sample, and a cycle with no samples
            // at all already says so through SilentSources.
            return;
        }

        try
        {
            _observationStore.Append(observations);

            // Cleared, not left behind. One restart of the database at 02:00
            // otherwise put that message on every result until the service was
            // restarted: the worker warned every thirty seconds that samples
            // could not be recorded while they were being recorded perfectly.
            // A fabricated failure is the mirror of a fabricated zero and worse
            // in one respect -- it teaches an operator to ignore the one
            // message that means the history really does have a hole.
            _lastStorageFailure = null;
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
    /// file on the server. See ADR-0005. It describes the last attempt and
    /// nothing else: the next attempt that succeeds clears it, and a cycle with
    /// nothing to write leaves it alone.
    /// </remarks>
    private string? _lastStorageFailure;

    /// <summary>
    /// Performs one of this cycle's store writes so that its failure costs only
    /// that write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Collection sources have had this from the beginning
    /// (<c>SourceRunner</c>), the sample write has had it
    /// (<see cref="StoreObservations"/>), and analysis rules were given it in
    /// <c>GuardedRule</c>. The cycle's own writes had nothing. A statement
    /// timeout inside <c>Merge</c> ended the observation pass with every source
    /// already read and the samples sitting in memory, before the one piece of
    /// code written to protect them ran; nothing reached the product and one
    /// line reached a log file on the server.
    /// </para>
    /// <para>
    /// The failure becomes an alert rather than another field on the result,
    /// unlike <see cref="MonitoringCycleResult.StorageFailure"/>. That field
    /// earns its place because it describes this cycle's samples, which the
    /// result carries beside it. These describe state that outlives the cycle,
    /// and an operator is the only one who can act on them -- so they go where
    /// ADR-0005 puts "we are not recording": into the product. Being an
    /// ordinary alert also means each one is reconciled in its own scope and
    /// resolves by itself on the first cycle the write survives, so no part of
    /// this has to remember to clear anything.
    /// </para>
    /// <para>
    /// Not every write is guarded, and that is the point rather than an
    /// oversight. <see cref="Reconcile"/> and the marking in
    /// <see cref="NotifyAsync"/> are left to throw: a cycle whose
    /// reconciliation failed has decided nothing, and carrying on would mean
    /// inventing an empty result, which reads as "nothing is wrong" -- an
    /// all-clear nobody measured, and the one thing worse than a failed cycle.
    /// There is nowhere to put an alert about it either, the store that would
    /// hold it being the store that just failed.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<AlertDefinition> Guarded(
        string writeId, string what, Action write)
    {
        try
        {
            write();

            return [];
        }
        catch (OperationCanceledException)
        {
            // The cycle is shutting down, not the store misbehaving. An alert
            // that fires on every restart is one nobody reads.
            throw;
        }
#pragma warning disable CA1031 // Justified: see the remarks above. A write
        // crosses a network to a database and may throw anything, and the one
        // outcome that must not happen is this cycle's measurements going
        // unreported because a different subsystem could not be written to.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return [WriteFailed(writeId, what, ex)];
        }
    }

    /// <summary>
    /// Says that one write did not land, and what that leaves stale.
    /// </summary>
    /// <param name="writeId">
    /// Stable across releases and distinct per write: it is part of the
    /// fingerprint, so two writes failing must be two alerts or fixing one
    /// would resolve the other's while it was still broken.
    /// </param>
    private static AlertDefinition WriteFailed(string writeId, string what, Exception error) =>
        new()
        {
            Fingerprint = AlertFingerprint.Create(
                "platform", WriteFailedTitle, WriteFailedCategory, writeId, "store-write-failed"),

            // Warning, for the reason an unreachable collector is one: nothing
            // says the estate is broken, only that part of what we measured was
            // not written down.
            Severity = AlertSeverity.Warning,
            Title = WriteFailedTitle,
            Description =
                $"Writing {what} threw {error.GetType().Name} and was skipped: {error.Message} " +
                "The rest of this cycle ran normally, but what is stored is now older than what " +
                "was measured, and every later cycle reasons from the stored copy.",
            Category = WriteFailedCategory,
            Source = "platform",
            IsDerived = true,
        };

    private const string WriteFailedTitle = "State could not be saved";

    private const string WriteFailedCategory = "Configuration";

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
    /// <para>
    /// Reading and writing the same scope is the whole of the safety here: the
    /// reconciler resolves anything it was given but did not see, so a cycle
    /// that read the other scope's instances would clear them. See
    /// <see cref="AlertScopes"/>.
    /// </para>
    /// <para>
    /// This method used to stamp the observations on the way in, because the
    /// collection pipelines stamped what the collectors reported and nothing
    /// stamped what the cycle added to them — the analysis rules and
    /// <see cref="Guarded"/> both produce definitions with no scope, and
    /// <c>AlertLifecycle</c> copies whatever it is given. That stamp is gone,
    /// not because the problem went away but because it moved to the one place
    /// that can answer it for every producer at once: the reconciler is handed
    /// <paramref name="scope"/> and stamps what it returns. A definition
    /// arriving here unscoped is now simply a definition, and the cycle no
    /// longer has an opinion about the field at all.
    /// </para>
    /// </remarks>
    private AlertReconciliationResult Reconcile(
        string scope,
        IReadOnlyList<AlertDefinition> observed,
        MonitoringOptions options,
        DateTimeOffset now) =>
        _alertStore.Reconcile(scope, (stored, flaps) => AlertReconciler.Reconcile(
            new AlertReconciliationRequest
            {
                // The same value the store is asked to file under, passed once
                // and used for both. This is the whole of the cycle's part in
                // scoping now: it says which evaluation is running, and the
                // reconciler stamps what comes out of it.
                Scope = scope,
                Observed = observed,
                Stored = stored,
                FlapHistories = flaps,
                Hysteresis = options.Hysteresis,
                Flap = options.Flap,
                // Read per cycle, not per process. A window declared while the
                // service is running has to take effect on the next cycle, and
                // one that has ended has to stop taking effect on the next one
                // too — suppression is recomputed every time rather than
                // stamped on once.
                MaintenanceWindows = _maintenance.ActiveAt(now),
                NowUtc = now,
            }));

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
