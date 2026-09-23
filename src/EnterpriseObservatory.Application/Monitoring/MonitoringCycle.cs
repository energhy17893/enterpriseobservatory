using System.Diagnostics;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Analysis;
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

    /// <summary>
    /// Per answering source: alerts in its snapshot, alerts of that source
    /// handed to reconciliation, and instances of that source held after it.
    /// </summary>
    /// <remarks>
    /// Diagnostic (S3 follow-up): the three side by side say which layer
    /// loses a source's alert, if one does.
    /// </remarks>
    public IReadOnlyList<SourceAlertCount> AlertsBySource { get; init; } = [];

    /// <summary>
    /// Sources that returned a snapshot this cycle.
    /// </summary>
    /// <remarks>
    /// Not merely the complement of <see cref="SilentSources"/>: a vCenter
    /// removed from the product is in neither the configured list nor this
    /// one, and its entities stay in the graph as last read. Whatever is
    /// judged from them is not current, and only this list can say so.
    /// </remarks>
    public IReadOnlyList<string> ReportingSources { get; init; } = [];

    /// <summary>
    /// How long this pass took, wall clock, from the first collection call to
    /// the reconciliation it fed.
    /// </summary>
    /// <remarks>
    /// Package D (self-monitoring): the runner produces this, not each
    /// collector, per ADR-0025. Measured with a <see cref="Stopwatch"/>
    /// rather than <see cref="IClock"/> so a fake clock in a test does not
    /// make every cycle look instantaneous or, worse, negative.
    /// </remarks>
    public TimeSpan CycleDuration { get; init; }

    /// <summary>
    /// <c>alert_history</c> rows this cycle's reconciliation appended. See
    /// <see cref="AlertReconciliationResult.TransitionsAppended"/>.
    /// </summary>
    public int TransitionsAppended { get; init; }

    /// <summary>
    /// Verdicts this cycle clamped to <see cref="AlertLifecycleState.Unknown"/> by the
    /// age rule (ADR-0026 §Z3) rather than by a source going silent. See
    /// <see cref="AlertReconciliationResult.AgeClampedToUnknown"/>.
    /// </summary>
    public int AgeClampedToUnknown { get; init; }
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
    IClock clock,
    IEventStore events,
    ObservationStoreQueue? storeQueue = null)
{
    /// <summary>
    /// The bounded queue in front of the observation store (F5, ADR-0025 §6).
    /// </summary>
    /// <remarks>
    /// Held for the process's life by the host, so samples a failed write
    /// could not take wait for the next cycle rather than being lost with
    /// this one. A cycle built without one (tests) gets its own, with the
    /// default limits and no gap record for what it drops.
    /// </remarks>
    private readonly ObservationStoreQueue _storeQueue = storeQueue ?? new ObservationStoreQueue(
        (observations ?? throw new ArgumentNullException(nameof(observations))).Append,
        clock ?? throw new ArgumentNullException(nameof(clock)));

    /// <summary>
    /// Where collected vCenter events are read from, for the rules. Handed
    /// on only as its read side.
    /// </summary>
    private readonly IEventReader _events =
        events ?? throw new ArgumentNullException(nameof(events));

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

    /// <summary>
    /// This cycle's own rule instances, so a rule that keeps state across
    /// cycles (<see cref="StorageLatencyBlindSpotRule"/>'s window) keeps it for
    /// this cycle's lifetime — the process's, in the host — and no other.
    /// </summary>
    private readonly IReadOnlyList<IAnalysisRule> _rules = AnalysisRules.Create();

    /// <summary>Re-reads inventory and folds it into the graph.</summary>
    /// <param name="disabledInstanceIds">
    /// Connections an operator switched off (N1, ADR-0026). They are not in
    /// <paramref name="sources"/> — the registry never builds a collector for
    /// one — so without this the "collection:inventory" producer never signs
    /// for their "Collector unreachable" fingerprint and a previously open one
    /// stays open and stale forever, instead of resolving.
    /// </param>
    public async Task<MonitoringCycleResult> RunInventoryAsync(
        IReadOnlyList<IInventorySource> sources,
        MonitoringOptions options,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? disabledInstanceIds = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        var disabled = disabledInstanceIds ?? [];

        var stopwatch = Stopwatch.StartNew();
        var now = _clock.UtcNow;

        var cycle = await _inventory
            .RunAsync(
                sources,
                _healthStore.Current,
                options.Collection.ForInterval(options.InventoryInterval),
                cancellationToken)
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

        // Annotations fold onto entities other sources own (ADR-0027); they
        // never open one. Two connections to one federation are settled here,
        // with an alert, rather than by Merge's throw stopping the cycle.
        var (annotations, duplicateConnections) = AnnotationClaims.Settle(
            [.. cycle.Snapshots.SelectMany(s => s.Annotations)]);

        var graph = _graphStore.Current.Merge(
            entities, relationships, reporting, now, options.EntityRetention, annotations);

        graph = graph with { Relationships = [.. graph.Relationships, .. ResolveIdentity(graph, now)] };

        // The order is what made this one dangerous. The graph is written
        // before reconciliation, so a throw here took the cycle's collection
        // alerts with it -- "Collector unreachable" included. The product went
        // quiet about vCenters it could not reach because a different subsystem
        // could not write, which is the failure this cycle exists to report.
        var graphFailure = Guarded(
            "entity-graph", "the topology", () => _graphStore.Replace(graph));

        // Capacity readings the inventory carried, through the store queue as
        // current state (F5b): a failed write keeps them for the next drain
        // instead of losing them, a newer reading replaces an older one still
        // waiting, and a drop records no gap — vCenter keeps no history of a
        // datastore's capacity to read again. Drained here, current state
        // only, before the rules run, so a rule reading a datastore's history
        // sees this cycle's point; the metric batches waiting beside them are
        // the metric cycle's to accept, with their marks. Not through
        // StoreObservations: that one reports on the metric cycle's result,
        // and letting this cycle clear or overwrite it would make the metric
        // cycle's storage message say something about a write it did not
        // make. A failure here becomes an inventory-scoped alert instead,
        // which resolves by itself on the first cycle whose write lands.
        IReadOnlyList<Observation> samples = [.. cycle.Snapshots.SelectMany(s => s.Observations)];

        foreach (var snapshot in cycle.Snapshots)
        {
            _storeQueue.EnqueueCurrentState(snapshot.SourceInstanceId, snapshot.Observations);
        }

        IReadOnlyList<AlertDefinition> sampleFailure = samples.Count == 0
            ? []
            : Guarded(
                "observations:inventory",
                "the capacity readings taken with the inventory",
                () =>
                {
                    if (_storeQueue.Drain(currentStateOnly: true).Failure is { } failure)
                    {
                        throw new InvalidOperationException(
                            $"{failure} They wait in the store queue for the next write.");
                    }
                });

        // Guarded like every other rule: a bug in counting paths must cost
        // the path count and not this cycle's "Collector unreachable".
        // Given the graph as this cycle merged it rather than the store's
        // copy, so that a failed write leaves the rules reasoning about
        // what was actually just read.
        var analysis = Analyse(AlertScopes.Inventory, RuleScope.Inventory, new RuleContext
        {
            Snapshots = cycle.Snapshots,
            ReadGraph = () => graph,
            NowUtc = now,
            Options = options,
            Series = _observationStore,
            Events = _events,
        });

        IReadOnlyList<AlertDefinition> observed =
        [
            .. coverageFailures,
            .. cycle.Snapshots.SelectMany(s => s.Alerts),
            .. duplicateConnections,
            .. cycle.CollectionAlerts,
            .. healthFailure,
            .. graphFailure,
            .. sampleFailure,
            .. analysis.Failures,
        ];

        // A vCenter that did not answer has told us nothing, for the reason
        // EntityGraph.Merge keeps its entities: we did not look. Judged by the
        // entity's owner in the graph this cycle merged, which is the same
        // graph that decided the entities stay.
        // Who ran, and so whose silence is an absence (ADR-0026): a direct
        // producer that did not run this cycle has told us nothing, and its
        // alerts stay open, stale. The sample write is the case that made it
        // matter -- with no capacity reading there is no write, and a
        // "could not be saved" alert used to resolve on a write never tried.
        IReadOnlyList<ProducerRun> ran =
        [
            ProducerRun.For(
                "collection:inventory",
                sources.Select(s => SourceRunner.UnreachableFingerprint(s.InstanceId, CollectorRole.Inventory))
                    // Disabled connections are never in `sources` -- the
                    // registry builds no collector for one -- but this cycle
                    // still speaks for them: it looked at the roster and
                    // deliberately did not ask. Signing here is what lets a
                    // "Collector unreachable" raised before it was disabled
                    // resolve instead of staying open and stale forever.
                    .Concat(disabled.Select(id =>
                        SourceRunner.UnreachableFingerprint(id, CollectorRole.Inventory)))),
            .. cycle.Snapshots.Select(s => ProducerRun.Where(
                $"inventory:{s.SourceInstanceId}", f => f.HasSource(s.SourceInstanceId))),
            .. cycle.Snapshots.Select(s => Wrote($"coverage:{s.SourceInstanceId}")),
            Wrote("collector-health:inventory"),
            Wrote("entity-graph"),
            ProducerRun.Where(AnnotationClaims.Producer, f => f.HasSource(AnnotationClaims.Producer)),
            .. samples.Count == 0 ? Array.Empty<ProducerRun>() : [Wrote("observations:inventory")],
            RulesRan(RuleScope.Inventory),
        ];

        var reconciliation = Reconcile(
            AlertScopes.Inventory,
            observed,
            ran,
            analysis.Evaluations,
            new EvidenceSources
            {
                Reporting = reporting,
                DisabledConnections = disabled,
                OwnerOf = entity => graph.Entities.TryGetValue(entity, out var e) ? e.SourceInstanceId : null,
                IsVanished = entity => graph.Entities.TryGetValue(entity, out var e) &&
                                       e.ObservationState == ObservationState.Vanished,
            },
            options,
            options.InventoryInterval,
            now);

        await NotifyAsync(AlertScopes.Inventory, reconciliation, cancellationToken)
            .ConfigureAwait(false);

        return new MonitoringCycleResult
        {
            AtUtc = now,
            Visible = VisibleInbox(),
            ToNotify = reconciliation.ToNotify,
            Observations = samples,
            ActiveEntities = graph.Active.Count(),
            VanishedEntities = graph.Vanished.Count(),
            SilentSources = silent,
            ReportingSources = reporting,
            AlertsBySource =
            [
                .. cycle.Snapshots.Select(s => new SourceAlertCount(
                    s.SourceInstanceId,
                    s.Alerts.Count,
                    observed.Count(a => a.Source == s.SourceInstanceId),
                    reconciliation.Instances.Count(i => i.Source == s.SourceInstanceId))),
            ],
            CycleDuration = stopwatch.Elapsed,
            TransitionsAppended = reconciliation.TransitionsAppended,
            AgeClampedToUnknown = reconciliation.AgeClampedToUnknown,
        };
    }

    /// <summary>Samples metrics and folds the resulting alerts in.</summary>
    /// <param name="disabledInstanceIds">See <see cref="RunInventoryAsync"/>'s parameter of the same name.</param>
    public async Task<MonitoringCycleResult> RunObservationsAsync(
        IReadOnlyList<IObservationSource> sources,
        MonitoringOptions options,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? disabledInstanceIds = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        var disabled = disabledInstanceIds ?? [];

        var stopwatch = Stopwatch.StartNew();
        var now = _clock.UtcNow;

        var cycle = await _observations
            .RunAsync(
                sources,
                _healthStore.Current,
                options.Collection.ForInterval(options.ObservationInterval),
                cancellationToken)
            .ConfigureAwait(false);

        // Every source has already been read and the samples are in memory. A
        // pool exhaustion or a statement timeout in here used to end the pass
        // before StoreObservations -- the one piece of code written to keep
        // those samples -- was ever reached.
        var healthFailure = Guarded(
            "collector-health:metrics",
            "the collectors' health",
            () => _healthStore.Merge(cycle.Health));

        // Through the store queue (F5, ADR-0025 §6): each batch's current
        // values and earlier samples as one item, kept or lost together, and
        // written oldest first — what an earlier failed write left behind
        // goes before this cycle's.
        foreach (var batch in cycle.Batches)
        {
            _storeQueue.Enqueue(batch);
        }

        var drain = StoreObservations();

        // Only once a batch is accepted: its source's marks and gap fill
        // points move past what was kept, never past what was merely read
        // (T0.4) — and not in this cycle's thread either: the runner applies
        // the advance inside the source's next read slot. A batch accepted
        // now may be one an earlier cycle read. Guarded per source, so one
        // source's gap record failing costs that bookkeeping and nothing else.
        var acceptedNow = drain.Accepted.Where(b => b.Slot is not null).ToList();

        IReadOnlyList<AlertDefinition> bookkeepingFailures =
        [
            .. acceptedNow.SelectMany(b => Guarded(
                $"collection-marks:{b.SourceInstanceId}",
                $"the collection marks and gap record of '{b.SourceInstanceId}'",
                () => b.Slot!.Accept(b))),
        ];

        // Only sources that actually answered may have their entities' metric
        // alerts judged on this cycle's silence. A source we could not reach
        // told us nothing about whether the fault it reported last cycle is
        // still there -- the same rule RunInventoryAsync applies to entities
        // and coverage, applied here to what the metric rules found. Read
        // from the graph as it stands now: this cycle merges nothing into it,
        // so a discovery made by the inventory cycle is visible immediately
        // and nothing here can go stale within a single pass.
        var graph = _graphStore.Current;
        var answered = cycle.Batches.Select(b => b.SourceInstanceId).ToList();
        var silent = sources
            // A source with no observation role (IRoleNotApplicable, ADR-0026)
            // never answers and never will; it is inventory-only, not silent.
            .Where(s => s is not IRoleNotApplicable)
            .Select(s => s.InstanceId)
            .Where(id => !answered.Contains(id, StringComparer.Ordinal))
            .ToList();

        // What the collectors could not read, and what the numbers themselves
        // say. Both belong to this scope because both are decided by this
        // cycle: reconciliation treats what it is given as the whole truth, so
        // a rule evaluated here must have its alerts reconciled here or the
        // next pass would resolve them.
        // Each rule is guarded, so a bug in one costs that rule and nothing
        // else. Collection sources and storage have always been isolated this
        // way; rules were the one part of the cycle that could still take the
        // whole thing down with them.
        // The store's graph, read by each rule that asks for it inside that
        // rule's guard: this cycle merged nothing.
        var analysis = Analyse(AlertScopes.Observation, RuleScope.Metric, new RuleContext
        {
            Observations = cycle.Observations,
            ReadGraph = () => graph,
            NowUtc = now,
            Options = options,
            Series = _observationStore,
            Events = _events,
        });

        IReadOnlyList<AlertDefinition> observed =
        [
            .. cycle.CollectionAlerts,
            .. healthFailure,
            .. bookkeepingFailures,
            .. SamplesDropped(_storeQueue.Snapshot(), now),
            .. analysis.Failures,
        ];

        // A source that did not answer has told us nothing about whether the
        // fault it reported last cycle is still there -- the same rule
        // RunInventoryAsync applies to entities and coverage. Only the sources
        // that answered are evidence; a verdict about an entity of any other
        // is unknown, which keeps an open alarm open.
        // Who ran (ADR-0026): the collector runner attempted every source,
        // a batch was read from each that answered, and the bookkeeping was
        // written only for what was kept.
        IReadOnlyList<ProducerRun> ran =
        [
            ProducerRun.For(
                "collection:metrics",
                sources.Select(s => SourceRunner.UnreachableFingerprint(s.InstanceId, CollectorRole.Observation))
                    .Concat(disabled.Select(id =>
                        SourceRunner.UnreachableFingerprint(id, CollectorRole.Observation)))),
            .. cycle.Batches.Select(b => ProducerRun.For(
                $"detail-level:{b.SourceInstanceId}",
                ObservationCollectionPipeline.DetailLevelFingerprint(b.SourceInstanceId))),
            Wrote("collector-health:metrics"),
            .. acceptedNow
                .Select(b => b.SourceInstanceId)
                .Distinct(StringComparer.Ordinal)
                .Select(id => Wrote($"collection-marks:{id}")),
            ProducerRun.For("store-queue", SamplesDroppedFingerprint),
            RulesRan(RuleScope.Metric),
        ];

        var reconciliation = Reconcile(
            AlertScopes.Observation,
            observed,
            ran,
            analysis.Evaluations,
            new EvidenceSources
            {
                Reporting = answered,
                DisabledConnections = disabled,
                OwnerOf = entity => graph.Entities.TryGetValue(entity, out var e) ? e.SourceInstanceId : null,
                IsVanished = entity => graph.Entities.TryGetValue(entity, out var e) &&
                                       e.ObservationState == ObservationState.Vanished,
            },
            options,
            options.ObservationInterval,
            now);

        await NotifyAsync(AlertScopes.Observation, reconciliation, cancellationToken)
            .ConfigureAwait(false);

        return new MonitoringCycleResult
        {
            AtUtc = now,
            Visible = VisibleInbox(),
            ToNotify = reconciliation.ToNotify,
            Observations = cycle.Observations,
            StorageFailure = _lastStorageFailure,
            ActiveEntities = graph.Active.Count(),
            VanishedEntities = graph.Vanished.Count(),
            SilentSources = silent,
            CycleDuration = stopwatch.Elapsed,
            TransitionsAppended = reconciliation.TransitionsAppended,
            AgeClampedToUnknown = reconciliation.AgeClampedToUnknown,
        };
    }

    /// <summary>
    /// Runs every registered rule of one scope, each so that its failure costs
    /// only that rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Collection sources and storage have always been isolated this way;
    /// rules were the one part of the cycle that could still take the whole
    /// thing down with them. Order is registration order — see
    /// <see cref="AnalysisRules"/>.
    /// </para>
    /// <para>
    /// Each rule is told which alerts it holds in this scope — read here, once,
    /// before any rule runs — so a rule can say they are gone, and so a rule
    /// that throws keeps them open rather than losing them.
    /// </para>
    /// </remarks>
    private Analysis Analyse(string alertScope, RuleScope scope, RuleContext context)
    {
        var held = _alertStore.InstancesIn(alertScope)
            .Where(i => i.RuleId is not null)
            .GroupBy(i => i.RuleId!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<HeldAlert>)[.. g.Select(i => new HeldAlert(i.Fingerprint, i.Entity))],
                StringComparer.Ordinal);

        IReadOnlyList<HeldAlert> HeldBy(string ruleId) => held.GetValueOrDefault(ruleId) ?? [];

        context = context with { HeldBy = HeldBy };

        var evaluations = new List<RuleEvaluation>();
        var failures = new List<AlertDefinition>();

        foreach (var rule in _rules.Where(r => r.Scope == scope))
        {
            var guarded = GuardedRule.Run(rule.RuleId, () => rule.Evaluate(context), HeldBy(rule.RuleId));

            evaluations.Add(new RuleEvaluation(rule.RuleId, rule.Resolution, guarded.Verdicts));
            failures.AddRange(guarded.Failures);
        }

        return new Analysis(evaluations, failures);
    }

    /// <summary>What one scope's rules concluded, and which of them failed.</summary>
    private sealed record Analysis(
        IReadOnlyList<RuleEvaluation> Evaluations,
        IReadOnlyList<AlertDefinition> Failures);

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
    /// <returns>Whether everything given is now in the store -- true when there was nothing to write.</returns>
    private StoreQueueDrain StoreObservations()
    {
        var drain = _storeQueue.Drain();

        if (!drain.Attempted)
        {
            // Deliberately neither set nor cleared. Nothing was written, so
            // nothing new is known about whether writing works, and clearing
            // here would report "we are keeping your samples" on the strength
            // of an attempt never made -- the same fabrication as a zero that
            // was never measured. The cost of holding it is that a cycle with
            // nothing to store repeats the last real failure; that is bounded
            // by the first cycle that has a sample, and a cycle with no samples
            // at all already says so through SilentSources.
            return drain;
        }

        // Cleared, not left behind, when the write lands. One restart of the
        // database at 02:00 otherwise put that message on every result until
        // the service was restarted: the worker warned every thirty seconds
        // that samples could not be recorded while they were being recorded
        // perfectly. A fabricated failure is the mirror of a fabricated zero
        // and worse in one respect -- it teaches an operator to ignore the one
        // message that means the history really does have a hole. The samples
        // a failed write could not take are still queued, not lost.
        _lastStorageFailure = drain.Failure;
        return drain;
    }

    /// <summary>
    /// Says so while the store queue has recently had to drop samples.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An ordinary alert, reconciled in this scope, so it reaches the inbox and
    /// the notifier like any other. Raised for as long as the last drop is
    /// within the queue's age limit, then resolved by itself: the counters
    /// behind it are cumulative for the process, and an alert that could only
    /// clear on a restart is one nobody reads.
    /// </para>
    /// <para>
    /// It says how much of the drop was recorded as a gap the source is
    /// reading again, and how much could not be: the first is a refill in
    /// progress, the second a loss.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<AlertDefinition> SamplesDropped(StoreQueueSnapshot queue, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(queue);

        if (queue.LastDropUtc is not { } last || now - last > queue.MaxAge)
        {
            return [];
        }

        return
        [
            new AlertDefinition
            {
                Fingerprint = SamplesDroppedFingerprint,
                Severity = AlertSeverity.Warning,
                Title = SamplesDroppedTitle,
                Description =
                    $"The store could not keep up and the queue in front of it dropped {queue.DroppedRows} " +
                    $"sample row(s) since the service started: {queue.DroppedOverBudgetRows} over its " +
                    $"{queue.BudgetBytes / StoreQueueLimits.BytesPerMegabyte} MiB budget and " +
                    $"{queue.DroppedTooOldRows} older than {queue.MaxAge.TotalMinutes:0} minutes and " +
                    $"{queue.DroppedCurrentStateRows} capacity reading(s), which are current state and not refilled. " +
                    $"{queue.RecordedAsGapRows} were recorded as a gap the source is reading again, " +
                    $"{queue.PendingGapRows} wait for the store to record theirs, and " +
                    $"{queue.CouldNotBeFilledRows} could not be filled. " +
                    $"Last write failure: {queue.LastFailure ?? "none"}.",
                Category = WriteFailedCategory,
                Source = "platform",
                IsDerived = true,
            },
        ];
    }

    private const string SamplesDroppedTitle = "Samples dropped before they could be stored";

    internal static readonly AlertFingerprint SamplesDroppedFingerprint =
        AlertFingerprint.Create("platform", SamplesDroppedTitle, WriteFailedCategory, "store-queue", "samples-dropped");

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
            Fingerprint = WriteFailedFingerprint(writeId),

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

    /// <summary>The fingerprint of one write's "could not be saved" alert.</summary>
    private static AlertFingerprint WriteFailedFingerprint(string writeId) =>
        AlertFingerprint.Create("platform", WriteFailedTitle, WriteFailedCategory, writeId, "store-write-failed");

    /// <summary>The signature of a write that was attempted this cycle.</summary>
    private static ProducerRun Wrote(string writeId) =>
        ProducerRun.For($"write:{writeId}", WriteFailedFingerprint(writeId));

    /// <summary>
    /// Every rule of the scope ran under its guard, so each speaks for its own
    /// "Analysis rule failed" alert.
    /// </summary>
    private static ProducerRun RulesRan(RuleScope scope) =>
        ProducerRun.For("analysis", AnalysisRules.For(scope).Select(r => GuardedRule.FailedFingerprint(r.RuleId)));

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
        IReadOnlyList<ProducerRun> ran,
        IReadOnlyList<RuleEvaluation> evaluations,
        EvidenceSources sources,
        MonitoringOptions options,
        TimeSpan interval,
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
                ProducersRun = ran,
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

                // An alarm of a rule that moved to the continuity catalogue
                // (K2) carries that rule's id and is given no verdict any
                // more, so it is "not reported" and stays open until the
                // continuity evaluation resolves it as moved.
                Evaluations = evaluations,
                Sources = sources,

                // Every rule the product registers, across scopes: an open
                // alert of any other rule is over, as "rule retired".
                RegisteredRules = RegisteredRules,

                // Read from the retention policy, not copied: if ADR-0017
                // changes raw retention, the limit moves with it.
                RawRetention = options.Retention.Raw,
                EvidenceLimit = EvidenceLimit(options, interval),
            }));

    private static readonly IReadOnlyCollection<string> RegisteredRules =
        AnalysisRules.All.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The age clamp: 2 × the scope's interval + its read budget (design note
    /// §1.3). Computed from the collection policy; never a number of its own.
    /// </summary>
    internal static TimeSpan EvidenceLimit(MonitoringOptions options, TimeSpan interval) =>
        (2 * interval) + options.Collection.ForInterval(interval).SourceTimeout;

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

/// <summary>One source's alerts through one inventory cycle. See <see cref="MonitoringCycleResult.AlertsBySource"/>.</summary>
public sealed record SourceAlertCount(string Source, int InSnapshot, int PassedToReconciler, int HeldAfter);
