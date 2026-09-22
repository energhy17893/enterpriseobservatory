using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Health;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Api.Projections;

/// <summary>
/// Turns the one model into the views the interface needs.
/// </summary>
/// <remarks>
/// <para>
/// The governing rule of ADR-0007: however many screens there are, they are all
/// projections of one model. No view holds its own state, builds its own alert
/// list or computes its own health. The previous product broke this — the alert
/// page read lifecycle instances while vendor pages read the raw per-cycle
/// snapshot, so the same alert could be acknowledged on one screen and open on
/// another.
/// </para>
/// <para>
/// Read-only and stateless. It is given the stores and asked a question; it
/// keeps nothing between calls.
/// </para>
/// </remarks>
public sealed class ReadModel(
    IEntityGraphStore graphs,
    IAlertStateStore alerts,
    ICollectorHealthStore collectors,
    ICoverageStore coverage,
    IObservationStore observations,
    MonitoringOptions options,
    IClock clock,
    IComplianceStore? compliance = null,
    IReadOnlyList<ContinuityCheck>? continuityChecks = null,
    ICollectionGapStore? gaps = null,
    IOperationalMetricsStore? selfMetrics = null)
{
    /// <summary>
    /// Where the continuity findings live (ADR-0024); null reads as "never
    /// evaluated", which the continuity report says rather than showing zeros.
    /// </summary>
    private readonly IComplianceStore? _compliance = compliance;

    /// <summary>
    /// The continuity catalogue the report is placed by: production's unless a
    /// caller registers its own. The report reads it, never a list of ids.
    /// </summary>
    private readonly IReadOnlyList<ContinuityCheck> _continuityChecks =
        continuityChecks ?? ContinuityCatalogue.Production;

    /// <summary>
    /// Where source-level gap records live; null reads as "no gaps recorded"
    /// (Package D's metrics view). Optional for the same reason
    /// <see cref="_compliance"/> is: existing tests construct this model
    /// without it.
    /// </summary>
    private readonly ICollectionGapStore? _gaps = gaps;

    /// <summary>
    /// The runner's own last-cycle numbers; null reads as "nothing measured
    /// yet" (Package D's metrics view).
    /// </summary>
    private readonly IOperationalMetricsStore? _selfMetrics = selfMetrics;

    private readonly IEntityGraphStore _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
    private readonly IAlertStateStore _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));

    private readonly ICoverageStore _coverage =
        coverage ?? throw new ArgumentNullException(nameof(coverage));

    private readonly ICollectorHealthStore _collectors =
        collectors ?? throw new ArgumentNullException(nameof(collectors));

    private readonly IObservationStore _observations =
        observations ?? throw new ArgumentNullException(nameof(observations));

    /// <summary>
    /// Held for its retention windows, which decide what a window can be
    /// answered from.
    /// </summary>
    /// <remarks>
    /// Chosen here rather than in the store because picking a resolution needs
    /// two things the store deliberately does not hold: the retention policy —
    /// which <c>Compact</c> receives per call, so the store is stateless about
    /// it — and a clock, to know how far back the window reaches. Both are
    /// already here.
    /// </remarks>
    private readonly MonitoringOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The largest page the API will return.</summary>
    /// <remarks>
    /// Bounded here rather than trusted from the query string. An unbounded
    /// page is a request to serialise the entire estate, which is both a
    /// performance problem and the easiest denial of service in the product.
    /// </remarks>
    public const int MaxLimit = 500;

    // --- overview ---------------------------------------------------------

    public OverviewView Overview()
    {
        var graph = _graphs.Current;
        var visible = Visible();
        var health = _collectors.Current;

        var derived = EntityHealth.DeriveAll(graph.Active, _alerts.All);

        var byHealth = derived.Values
            .GroupBy(d => d.Health)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        // Every state appears, including the ones with no members. A missing
        // key reads as "no answer" in a client; a zero reads as "none", which
        // is what we mean.
        foreach (var state in Enum.GetValues<HealthState>())
        {
            byHealth.TryAdd(state.ToString(), 0);
        }

        return new OverviewView
        {
            GeneratedAtUtc = _clock.UtcNow,
            CriticalAlerts = visible.Count(a => a.Severity == AlertSeverity.Critical),
            WarningAlerts = visible.Count(a => a.Severity == AlertSeverity.Warning),
            UnacknowledgedAlerts = visible.Count(a => a.State == AlertLifecycleState.Open),
            SuppressedAlerts = visible.Count(a => a.SuppressedByWindowId is not null),
            FreshOpenAlerts = visible.Count(a => !a.IsStale),
            StaleOpenAlerts = visible.Count(a => a.IsStale),
            UnknownAlerts = _alerts.All.Count(a => a.IsConfirmed && a.State == AlertLifecycleState.Unknown),
            EntitiesByHealth = byHealth,
            EntitiesWithStaleHealth = derived.Values.Count(d => d.IsStale),
            VanishedEntities = graph.Vanished.Count(),
            FailingCollectors = health.Count(c => c.Health != HealthState.Healthy),
            OldestSuccessfulReadUtc = health.Count == 0
                ? null
                : health.Min(c => c.LastSuccessUtc),
        };
    }

    // --- self-metrics (Package D) ------------------------------------------

    /// <summary>
    /// The runner's own last-cycle numbers, for the overview screen's own-health
    /// card. See <see cref="SelfMetricsView"/> for what is and is not here.
    /// </summary>
    public SelfMetricsView SelfMetrics()
    {
        var gapCounts = _gaps?.CountsByState() ?? new Dictionary<CollectionGapState, int>();

        return new SelfMetricsView
        {
            GeneratedAtUtc = _clock.UtcNow,
            Inventory = ToView(_selfMetrics?.Inventory),
            Observation = ToView(_selfMetrics?.Observation),
            UnknownAlerts = _alerts.All.Count(a => a.IsConfirmed && a.State == AlertLifecycleState.Unknown),
            OpenGaps = gapCounts.GetValueOrDefault(CollectionGapState.Open),
            UnrecoverableGaps = gapCounts.GetValueOrDefault(CollectionGapState.Unrecoverable),
        };
    }

    private static CycleMetricsView ToView(CycleMetricsSnapshot? snapshot) => new()
    {
        AtUtc = snapshot?.AtUtc,
        DurationSeconds = snapshot?.Duration.TotalSeconds ?? 0,
        TransitionsAppended = snapshot?.TransitionsAppended ?? 0,
        AgeClampedToUnknown = snapshot?.AgeClampedToUnknown ?? 0,
    };

    // --- alerts -----------------------------------------------------------

    /// <summary>
    /// The inbox: every visible alert, worst first.
    /// </summary>
    /// <remarks>
    /// This is the flat list ADR-0007 §5.1 guarantees is always one click away.
    /// Event grouping is a view over it, never a replacement for it: no alert
    /// may live only inside a group.
    /// </remarks>
    public Page<AlertView> Alerts(
        AlertSeverity? severity = null,
        AlertLifecycleState? state = null,
        string? category = null,
        string? source = null,
        string? search = null,
        int offset = 0,
        int limit = 50)
    {
        var graph = _graphs.Current;

        // The Unknown state is outside the inbox and is listed under its own
        // filter only (ADR-0026 design note §2).
        var pool = state == AlertLifecycleState.Unknown
            ? [.. _alerts.All.Where(a => a.IsConfirmed && a.State == AlertLifecycleState.Unknown)]
            : Visible();

        var matching = pool
            .Where(a => severity is null || a.Severity == severity)
            .Where(a => state is null || a.State == state)
            .Where(a => category is null ||
                string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(a => source is null ||
                string.Equals(a.Source, source, StringComparison.OrdinalIgnoreCase))
            .Where(a => Matches(a, search))
            // Worst first, then most recent. An operator reads from the top and
            // must find the thing that matters there.
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.LastSeenUtc)
            .ToList();

        return Paged(matching, offset, limit, a => ToView(a, graph));
    }

    // --- reports ------------------------------------------------------------

    /// <summary>How far back a report looks when no range is given.</summary>
    private static readonly TimeSpan DefaultReportWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// The alert/finding report: open alerts plus whatever resolved within the
    /// chosen window. Serves both the printable page and the CSV export, so
    /// the two can never disagree about what a report contains.
    /// </summary>
    /// <remarks>
    /// Open alerts are included regardless of the range — an alert still
    /// firing belongs on the report no matter when it started, the same way
    /// the inbox works. Only the resolved half is windowed, by when it
    /// actually resolved rather than by <c>LastSeenUtc</c>, which a resolved
    /// alert does not update again.
    /// </remarks>
    public AlertReportView AlertsReport(
        AlertSeverity? severity = null,
        AlertLifecycleState? state = null,
        string? category = null,
        string? source = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null)
    {
        var to = toUtc ?? _clock.UtcNow;
        var from = fromUtc ?? to - DefaultReportWindow;
        var graph = _graphs.Current;

        var rows = ReportCandidates(from, to)
            .Where(a => severity is null || a.Severity == severity)
            .Where(a => state is null || a.State == state)
            .Where(a => category is null ||
                string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(a => source is null ||
                string.Equals(a.Source, source, StringComparison.OrdinalIgnoreCase))
            // Worst first, then most recent -- the same order the inbox uses.
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.LastSeenUtc)
            .Select(a => ToReportRow(a, graph))
            .ToList();

        return new AlertReportView
        {
            GeneratedAtUtc = _clock.UtcNow,
            FromUtc = from,
            ToUtc = to,
            Summary = Summarize(rows),
            Rows = rows,
        };
    }

    /// <summary>Open (in any of its live states) plus resolved-within-range.</summary>
    /// <remarks>
    /// <para>
    /// The open half from the store; the resolved half from the durable
    /// history (<see cref="IAlertStateStore.ResolvedBetween"/>). A resolved
    /// alert retires the cycle after it resolves, and until migration 14 its
    /// transitions went with it — so an alert that resolved an hour ago was on
    /// the report for thirty seconds and then was not (ADR-0026).
    /// </para>
    /// <para>
    /// Windowed by when it resolved, from the history rather than
    /// <c>LastSeenUtc</c>: a resolved alert is not observed again, so its
    /// last-seen time is when the condition was last true, not when it stopped
    /// being one.
    /// </para>
    /// </remarks>
    private IReadOnlyList<AlertInstance> ReportCandidates(DateTimeOffset from, DateTimeOffset to) =>
    [
        .. _alerts.All.Where(a => a.IsConfirmed && a.State != AlertLifecycleState.Resolved),
        .. _alerts.ResolvedBetween(from, to).Where(a => a.IsConfirmed),
    ];

    private static AlertReportRow ToReportRow(AlertInstance alert, EntityGraph graph)
    {
        string? name = null;
        EntityKind? kind = null;

        if (alert.Entity is { } entityId && graph.Entities.TryGetValue(entityId, out var entity))
        {
            name = entity.DisplayName;
            kind = entity.Kind;
        }

        var acknowledged = LastActorTransition(alert, AlertTransitionReason.OperatorAcknowledged);
        var cleared = LastActorTransition(alert, AlertTransitionReason.OperatorCleared);

        return new AlertReportRow
        {
            Severity = alert.Severity,
            Title = alert.Title,
            EntityName = name,
            EntityKind = kind,
            Category = alert.Category,
            Source = alert.Source,
            State = alert.State,
            FirstSeenUtc = alert.FirstSeenUtc,
            LastSeenUtc = alert.LastSeenUtc,
            AcknowledgedBy = acknowledged.By,
            AcknowledgedAtUtc = acknowledged.AtUtc,
            ClearedBy = cleared.By,
            ClearedAtUtc = cleared.AtUtc,
            IsDerived = alert.IsDerived,
        };
    }

    /// <summary>
    /// The last time an operator caused this transition, and who -- <c>null</c>
    /// when it never happened. Only operator-attributed reasons carry an
    /// actor; see <see cref="AlertTransition.Actor"/>.
    /// </summary>
    private static (string? By, DateTimeOffset? AtUtc) LastActorTransition(
        AlertInstance alert, AlertTransitionReason reason)
    {
        var transition = alert.History.LastOrDefault(t => t.Reason == reason);
        return transition is null ? (null, null) : (transition.Actor, transition.AtUtc);
    }

    private static AlertReportSummary Summarize(List<AlertReportRow> rows)
    {
        var bySeverity = rows.GroupBy(r => r.Severity.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var value in Enum.GetValues<AlertSeverity>())
        {
            bySeverity.TryAdd(value.ToString(), 0);
        }

        var byState = rows.GroupBy(r => r.State.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var value in Enum.GetValues<AlertLifecycleState>())
        {
            byState.TryAdd(value.ToString(), 0);
        }

        return new AlertReportSummary
        {
            BySeverity = bySeverity,
            ByState = byState,
            Total = rows.Count,
        };
    }

    /// <summary>
    /// The capacity report: every live datastore, its latest reading and the
    /// same fill-date estimate the datastore's own page shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The estimate is the expensive part -- a 720-point trend fit per
    /// datastore -- and it is read through <see cref="TimeToFull"/>, the same
    /// method the entity page calls, which goes through
    /// <c>DatastoreTimeToFull.Read</c>'s process-wide cache. A report of forty
    /// datastores costs one history query and, at most, one fresh fit each,
    /// not forty because a page happened to load in between.
    /// </para>
    /// <para>
    /// Vanished datastores are left off, the same choice the explorer makes by
    /// default: a datastore nobody has seen this cycle has no current reading
    /// to report.
    /// </para>
    /// </remarks>
    public CapacityReportView CapacityReport()
    {
        var now = _clock.UtcNow;
        var retention = _options.Retention;
        var graph = _graphs.Current;

        var rows = graph.Entities.Values
            .Where(e => e.Kind == EntityKind.Datastore && e.ObservationState != ObservationState.Vanished)
            .Select(e => ToCapacityReportRow(e, now, retention))
            // Soonest fill date first; a refusal sorts after every forecast.
            // Among the rest, worst (highest percent used) first.
            .OrderBy(r => r.TimeToFull.FullAtUtc ?? DateTimeOffset.MaxValue)
            .ThenByDescending(r => r.PercentUsed ?? -1)
            .ToList();

        return new CapacityReportView
        {
            GeneratedAtUtc = now,
            Summary = SummarizeCapacity(rows),
            Rows = rows,
        };
    }

    private CapacityReportRow ToCapacityReportRow(
        Entity datastore, DateTimeOffset now, SeriesRetentionPolicy retention)
    {
        var capacity = DatastoreTimeToFull.LatestCapacity(_observations, datastore.Id, now, retention);
        var free = LatestReading(datastore.Id, CapacityCounters.DatastoreFree, now, retention);
        var provisioned = LatestReading(datastore.Id, CapacityCounters.DatastoreProvisioned, now, retention);

        var used = capacity is { } c && free is { } f ? c - f : (double?)null;
        var percentUsed = used is { } u && capacity is > 0 ? u / capacity * 100 : (double?)null;
        var overcommitRatio = provisioned is { } p && capacity is > 0 ? p / capacity : (double?)null;

        return new CapacityReportRow
        {
            Name = datastore.DisplayName,
            DatastoreType = datastore.Settings.TryGetValue("type", out var type) ? type : null,
            Source = datastore.SourceInstanceId,
            CapacityBytes = capacity,
            UsedBytes = used,
            FreeBytes = free,
            PercentUsed = percentUsed,
            ProvisionedBytes = provisioned,
            OvercommitRatio = overcommitRatio,
            TimeToFull = TimeToFull(datastore.Id),
        };
    }

    /// <summary>
    /// The latest reading of one capacity counter, straight from the raw
    /// tier -- the same query shape as <c>DatastoreTimeToFull.LatestCapacity</c>,
    /// generalized to the other capacity series it does not cover. A single
    /// point at <c>MaxPoints = 1</c>, not the 720-point history the trend is
    /// fitted to, so this costs nothing extra per row.
    /// </summary>
    private double? LatestReading(
        EntityId entity, string counter, DateTimeOffset now, SeriesRetentionPolicy retention)
    {
        var result = _observations.Query(new SeriesQuery
        {
            Key = new SeriesKey(entity, counter, string.Empty),
            FromUtc = now - retention.Raw,
            ToUtc = now + TimeSpan.FromTicks(1),
            Resolution = SeriesResolution.Raw,
            MaxPoints = 1,
        });

        return result.Points.Count > 0 ? result.Points[^1].Last : null;
    }

    private static CapacityReportSummary SummarizeCapacity(List<CapacityReportRow> rows)
    {
        var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var noEstimate = 0;

        foreach (var row in rows)
        {
            if (row.TimeToFull.IsForecast || row.TimeToFull.Reason is not { } reason)
            {
                continue;
            }

            noEstimate++;
            byReason[reason] = byReason.GetValueOrDefault(reason) + 1;
        }

        return new CapacityReportSummary
        {
            TotalDatastores = rows.Count,
            TotalCapacityBytes = rows.Sum(r => r.CapacityBytes ?? 0),
            TotalUsedBytes = rows.Sum(r => r.UsedBytes ?? 0),
            TotalFreeBytes = rows.Sum(r => r.FreeBytes ?? 0),
            FillingWithin30Days = rows.Count(r =>
                r.TimeToFull is { IsForecast: true, Days: <= 30 }),
            FillingWithin7Days = rows.Count(r =>
                r.TimeToFull is { IsForecast: true, Days: <= 7 }),
            OvercommittedCount = rows.Count(r => r.OvercommitRatio is > 1),
            NoEstimateCount = noEstimate,
            NoEstimateByReason = byReason,
        };
    }

    // --- continuity report (M8.10) -----------------------------------------

    /// <summary>The continuity findings as stored, with the exceptions that decide their state.</summary>
    private (IReadOnlyList<ComplianceFinding> Findings, IReadOnlyList<ComplianceWaiver> Exceptions) Continuity()
    {
        if (_compliance is null)
        {
            return ([], []);
        }

        return (
            [.. _compliance.Findings.Where(f =>
                string.Equals(f.CatalogueRelease, ContinuityCatalogue.Release, StringComparison.Ordinal))],
            _compliance.Exceptions);
    }

    private static bool IsHa(string controlId) =>
        controlId.StartsWith("eo-cont.ha-", StringComparison.Ordinal);

    /// <summary>
    /// Where a control's findings sit in the report, read from the entity kind
    /// its check applies to -- the only thing the report knows about a control.
    /// </summary>
    private static ContinuityReportScope ScopeOf(EntityKind kind) => kind switch
    {
        EntityKind.VCenter => ContinuityReportScope.VCenter,
        EntityKind.Cluster => ContinuityReportScope.Cluster,
        _ => ContinuityReportScope.Entity,
    };

    /// <summary>
    /// The continuity report (M8.10), placed by the catalogue (K3): a vCenter
    /// section, one row per live cluster, and one summary row per control that
    /// applies to hosts, VMs or datastores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the <c>eo-continuity</c> findings (ADR-0024), never
    /// recomputed: the same rows the compliance screen shows. A finding is
    /// counted in its state -- failing, accepted, excepted, not evaluated,
    /// passing -- and a stale one also in <see cref="ContinuityStateCounts.Stale"/>,
    /// the way the compliance summary counts it. Zero is only "all clear"
    /// when the checks have run: until then the summary says so.
    /// </para>
    /// <para>
    /// No control id appears here. Each control is placed by its check's
    /// <see cref="IComplianceCheck.AppliesTo"/>, and a finding under a cluster
    /// reaches it through the graph's containment edges, so a control added to
    /// the catalogue grows the report without a change to this method.
    /// </para>
    /// </remarks>
    public ContinuityReportView ContinuityReport()
    {
        var graph = _graphs.Current;
        var now = _clock.UtcNow;
        var (findings, exceptions) = Continuity();

        var controls = _continuityChecks
            .Select(c => new ContinuityControlInfo
            {
                ControlId = c.Control.ControlId,
                Title = c.Control.Title,
                Citation = c.Control.Source,
                AppliesTo = c.Check.AppliesTo,
                Scope = ScopeOf(c.Check.AppliesTo),
            })
            .ToList();

        var clusterControls = controls.Where(c => c.Scope == ContinuityReportScope.Cluster).ToList();
        var vCenterControls = controls.Where(c => c.Scope == ContinuityReportScope.VCenter).ToList();
        var entityControls = controls.Where(c => c.Scope == ContinuityReportScope.Entity).ToList();

        var byEntity = findings.ToLookup(f => f.Entity);

        var clusters = graph.Entities.Values
            .Where(e => e.Kind == EntityKind.Cluster && e.ObservationState != ObservationState.Vanished)
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Whether HA configuration has ever actually been read for this
        // estate: a fresh install that has not reached the clusters yet must
        // read as "not collected", not as "every cluster passed".
        var haInputsCollected = clusters.Any(HasHaSettings);

        // The findings of entity-level controls, by the cluster they sit under.
        var entityControlIds = entityControls.Select(c => c.ControlId).ToHashSet(StringComparer.Ordinal);
        var clusterOf = ContainingClusters(graph);
        var underCluster = findings
            .Where(f => entityControlIds.Contains(f.ControlId))
            .Select(f => (Finding: f, Cluster: clusterOf(f.Entity)))
            .Where(x => x.Cluster is not null)
            .ToLookup(x => x.Cluster!.Value, x => x.Finding);

        var rows = clusters
            .Select(c => ToContinuityRow(c, graph, clusterControls, byEntity[c.Id], underCluster[c.Id], exceptions, now))
            .ToList();

        var vCenters = graph.Entities.Values
            .Where(e => e.Kind == EntityKind.VCenter && e.ObservationState != ObservationState.Vanished)
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(v => ToVCenterSection(v, vCenterControls, byEntity[v.Id], exceptions, now))
            .ToList();

        var byControl = findings.ToLookup(f => f.ControlId, StringComparer.Ordinal);

        var controlRows = entityControls
            .Select(c => ToControlRow(c, graph, byControl[c.ControlId], exceptions, now))
            .ToList();

        return new ContinuityReportView
        {
            GeneratedAtUtc = now,
            Summary = SummarizeContinuity(rows, findings, exceptions, now, haInputsCollected),
            Controls = controls,
            VCenters = vCenters,
            Rows = rows,
            ControlRows = controlRows,
        };
    }

    /// <summary>
    /// The common prefix every HA setting key is filed under, taken from the
    /// policy's own <see cref="ClusterHighAvailabilityPolicy.EnabledSetting"/>.
    /// </summary>
    private static readonly string HaSettingPrefix =
        ClusterHighAvailabilityPolicy.Default.EnabledSetting[
            ..(ClusterHighAvailabilityPolicy.Default.EnabledSetting.IndexOf('.', StringComparison.Ordinal) + 1)];

    private static bool HasHaSettings(Entity cluster) =>
        cluster.Settings.Keys.Any(k => k.StartsWith(HaSettingPrefix, StringComparison.Ordinal));

    private static ContinuityStateCounts Count(
        IEnumerable<ComplianceFinding> findings,
        IReadOnlyList<ComplianceWaiver> exceptions,
        DateTimeOffset now)
    {
        int failing = 0, accepted = 0, excepted = 0, notEvaluated = 0, passing = 0, stale = 0;

        foreach (var finding in findings)
        {
            switch (finding.StateAt(exceptions, now))
            {
                case FindingState.Failing: failing++; break;
                case FindingState.Accepted: accepted++; break;
                case FindingState.Excepted: excepted++; break;
                case FindingState.NotEvaluated: notEvaluated++; break;
                case FindingState.Passing: passing++; break;
            }

            if (finding.Stale)
            {
                stale++;
            }
        }

        return new ContinuityStateCounts
        {
            Failing = failing,
            Accepted = accepted,
            Excepted = excepted,
            NotEvaluated = notEvaluated,
            Passing = passing,
            Stale = stale,
        };
    }

    /// <summary>A failing, accepted or excepted finding: non-compliant, whoever owns it.</summary>
    private static bool IsAffected(ComplianceFinding finding, IReadOnlyList<ComplianceWaiver> exceptions, DateTimeOffset now) =>
        finding.StateAt(exceptions, now) is FindingState.Failing or FindingState.Accepted or FindingState.Excepted;

    private static string NameOf(EntityGraph graph, ComplianceFinding finding) =>
        graph.Entities.TryGetValue(finding.Entity, out var entity) ? entity.DisplayName : finding.EntityName;

    /// <summary>The distinct names, ordered, at most <see cref="ContinuityControlRow.MaxNamesListed"/>, and how many more.</summary>
    private static (IReadOnlyList<string> Names, int More) Listed(IEnumerable<string> names)
    {
        var all = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (
            [.. all.Take(ContinuityControlRow.MaxNamesListed)],
            Math.Max(0, all.Count - ContinuityControlRow.MaxNamesListed));
    }

    private static ContinuityReportRow ToContinuityRow(
        Entity cluster,
        EntityGraph graph,
        IReadOnlyList<ContinuityControlInfo> clusterControls,
        IEnumerable<ComplianceFinding> onCluster,
        IEnumerable<ComplianceFinding> under,
        IReadOnlyList<ComplianceWaiver> exceptions,
        DateTimeOffset now)
    {
        var own = onCluster.ToLookup(f => f.ControlId, StringComparer.Ordinal);
        var controls = clusterControls
            .Select(c => new ContinuityControlCounts { ControlId = c.ControlId, Counts = Count(own[c.ControlId], exceptions, now) })
            .ToList();

        var contained = under.ToList();
        var (names, more) = Listed(contained.Where(f => IsAffected(f, exceptions, now)).Select(f => NameOf(graph, f)));

        var clusterControlIds = clusterControls.Select(c => c.ControlId).ToHashSet(StringComparer.Ordinal);
        var totals = Count(
            onCluster.Where(f => clusterControlIds.Contains(f.ControlId)).Concat(contained), exceptions, now);

        return new ContinuityReportRow
        {
            ClusterId = cluster.Id.Value,
            ClusterName = cluster.DisplayName,
            Source = cluster.SourceInstanceId,
            HaSettingsCollected = HasHaSettings(cluster),
            Controls = controls,
            Contained = Count(contained, exceptions, now),
            ContainedAffectedNames = names,
            ContainedAffectedMore = more,
            Totals = totals,
            HasFailing = totals.Failing > 0,
        };
    }

    private ContinuityVCenterSection ToVCenterSection(
        Entity vCenter,
        IReadOnlyList<ContinuityControlInfo> vCenterControls,
        IEnumerable<ComplianceFinding> onVCenter,
        IReadOnlyList<ComplianceWaiver> exceptions,
        DateTimeOffset now)
    {
        var ids = vCenterControls.Select(c => c.ControlId).ToList();
        var own = onVCenter.Where(f => ids.Contains(f.ControlId, StringComparer.Ordinal)).ToList();
        var byControl = own.ToLookup(f => f.ControlId, StringComparer.Ordinal);

        return new ContinuityVCenterSection
        {
            VCenterId = vCenter.Id.Value,
            VCenterName = vCenter.DisplayName,
            Source = vCenter.SourceInstanceId,
            Controls =
            [
                .. vCenterControls.Select(c => new ContinuityControlCounts
                {
                    ControlId = c.ControlId,
                    Counts = Count(byControl[c.ControlId], exceptions, now),
                }),
            ],
            Findings =
            [
                .. own
                    .OrderBy(f => ids.IndexOf(f.ControlId))
                    .ThenBy(f => f.Subject, StringComparer.Ordinal)
                    .Select(f => ToView(f, exceptions, now)),
            ],
            // The alarms vCenter raised on itself: a root-folder alarm is
            // filed on the vCenter entity by the collector (#84).
            Alarms =
            [
                .. Visible()
                    .Where(a => a.Entity == vCenter.Id)
                    .OrderByDescending(a => a.Severity)
                    .ThenBy(a => a.FirstSeenUtc)
                    .Select(a => new ContinuityAlarmView
                    {
                        Title = a.Title,
                        Severity = a.Severity,
                        State = a.State,
                        IsStale = a.IsStale,
                        FirstSeenUtc = a.FirstSeenUtc,
                    }),
            ],
        };
    }

    private static ContinuityControlRow ToControlRow(
        ContinuityControlInfo control,
        EntityGraph graph,
        IEnumerable<ComplianceFinding> findings,
        IReadOnlyList<ComplianceWaiver> exceptions,
        DateTimeOffset now)
    {
        var all = findings.ToList();
        var (names, more) = Listed(
            all.Where(f => f.StateAt(exceptions, now) == FindingState.Failing).Select(f => NameOf(graph, f)));

        return new ContinuityControlRow
        {
            ControlId = control.ControlId,
            Title = control.Title,
            Citation = control.Citation,
            AppliesTo = control.AppliesTo,
            Counts = Count(all, exceptions, now),
            FailingNames = names,
            MoreFailing = more,
        };
    }

    /// <summary>
    /// The cluster an entity sits under, by the containment edges the
    /// inventory collector writes: a host <c>PartOf</c> its cluster, a VM
    /// <c>RunsOn</c> its host. Null when it is under none (a datastore, a
    /// standalone host).
    /// </summary>
    private static Func<EntityId, EntityId?> ContainingClusters(EntityGraph graph)
    {
        var parents = graph.Relationships
            .Where(r => r.Kind is RelationshipKind.PartOf or RelationshipKind.RunsOn)
            .ToLookup(r => r.From, r => r.To);

        var memo = new Dictionary<EntityId, EntityId?>();

        return start =>
        {
            if (memo.TryGetValue(start, out var known))
            {
                return known;
            }

            // Breadth-first up the edges; the visited set keeps a bad edge
            // from looping, since RunsOn is not checked acyclic with PartOf.
            var visited = new HashSet<EntityId> { start };
            var frontier = new Queue<EntityId>(parents[start]);
            EntityId? found = null;

            while (frontier.Count > 0)
            {
                var next = frontier.Dequeue();

                if (!visited.Add(next))
                {
                    continue;
                }

                if (graph.Entities.TryGetValue(next, out var entity) && entity.Kind == EntityKind.Cluster)
                {
                    found = next;
                    break;
                }

                foreach (var parent in parents[next])
                {
                    frontier.Enqueue(parent);
                }
            }

            memo[start] = found;
            return found;
        };
    }

    private ContinuityReportSummary SummarizeContinuity(
        List<ContinuityReportRow> rows,
        IReadOnlyList<ComplianceFinding> findings,
        IReadOnlyList<ComplianceWaiver> exceptions,
        DateTimeOffset now,
        bool haInputsCollected)
    {
        var byControl = _continuityChecks
            .Select(c => c.Control.ControlId)
            .ToDictionary(
                id => id,
                id => Count(findings.Where(f => f.ControlId == id), exceptions, now),
                StringComparer.Ordinal);

        var failing = rows.Where(r => r.HasFailing).ToList();
        var evaluated = findings.Count > 0;

        var notes = new List<string>();

        if (!evaluated)
        {
            notes.Add(
                "The continuity checks have not been evaluated yet, so a zero here means nothing was " +
                "looked at, not that everything passed.");
        }

        if (!haInputsCollected)
        {
            notes.Add(
                "Cluster HA/DRS configuration has not been read yet (not collected by this version, not " +
                "yet read since startup, or not permitted for the service account) -- see Coverage.");
        }

        return new ContinuityReportSummary
        {
            TotalClusters = rows.Count,
            Evaluated = evaluated,
            ByControl = byControl,
            Totals = Count(findings, exceptions, now),
            ClustersWithFailingCount = failing.Count,
            ClustersWithFailingNames = [.. failing.Select(r => r.ClusterName)],
            HaInputsCollected = haInputsCollected,
            Note = notes.Count == 0 ? null : string.Join(" ", notes),
        };
    }

    /// <summary>A continuity finding as a card or a report shows it.</summary>
    private ContinuityFindingView ToView(
        ComplianceFinding finding,
        IReadOnlyList<ComplianceWaiver> exceptions,
        DateTimeOffset now)
    {
        var control = _continuityChecks
            .FirstOrDefault(c => c.Control.ControlId == finding.ControlId)?.Control;

        return new ContinuityFindingView
        {
            ControlId = finding.ControlId,
            Title = control?.Title ?? finding.ControlId,
            Source = control?.Source ?? string.Empty,
            Subject = finding.Subject,
            SubjectLabel = finding.SubjectLabel,
            State = finding.StateAt(exceptions, now),
            Stale = finding.Stale,
            Expected = finding.Expected,
            Observed = finding.Observed,
            Reason = finding.Reason,
            AcceptedBy = finding.Acceptance?.By,
            AcceptedReason = finding.Acceptance?.Reason,
            LastEvaluatedUtc = finding.LastEvaluatedUtc,
        };
    }

    // --- entities ---------------------------------------------------------

    /// <summary>The entity explorer (tier 1 of ADR-0007).</summary>
    public Page<EntityView> Entities(
        EntityKind? kind = null,
        HealthState? health = null,
        string? source = null,
        string? search = null,
        bool includeVanished = false,
        int offset = 0,
        int limit = 50)
    {
        var graph = _graphs.Current;
        var counts = AlertCountsByEntity();
        var derived = EntityHealth.DeriveAll(graph.Entities.Values, _alerts.All);

        var matching = graph.Entities.Values
            .Where(e => includeVanished || e.ObservationState != ObservationState.Vanished)
            .Where(e => kind is null || e.Kind == kind)
            .Where(e => health is null || derived[e.Id].Health == health)
            .Where(e => source is null || string.Equals(e.SourceInstanceId, source, StringComparison.Ordinal))
            .Where(e => search is null ||
                e.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            // Worst first: the explorer is also a triage surface.
            .OrderByDescending(e => derived[e.Id].Health)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Paged(matching, offset, limit, e => ToView(e, counts, derived[e.Id]));
    }

    /// <summary>One entity with its edges and its alerts (tier 2 of ADR-0007).</summary>
    public EntityDetailView? Entity(string id)
    {
        var graph = _graphs.Current;
        var entityId = new EntityId(id);

        if (!graph.Entities.TryGetValue(entityId, out var entity))
        {
            return null;
        }

        var counts = AlertCountsByEntity();
        var derived = EntityHealth.DeriveAll(graph.Entities.Values, _alerts.All);

        var entityAlerts =
        (IReadOnlyList<AlertView>)
        [
            .. Visible()
                .Where(a => a.Entity == entityId)
                .OrderByDescending(a => a.Severity)
                .Select(a => ToView(a, graph)),
        ];

        return new EntityDetailView
        {
            Entity = ToView(entity, counts, derived[entityId]),
            Marks =
            [
                .. entity.Marks.Select(m => new IdentityMarkView
                {
                    Kind = m.Kind,
                    Value = m.Value,
                    Source = m.Source,
                }),
            ],
            Relationships = RelationshipsOf(graph, entityId, derived),
            Alerts = entityAlerts,
            TimeToFull = entity.Kind == EntityKind.Datastore ? TimeToFull(entityId) : null,
            HaScorecard = entity.Kind == EntityKind.Cluster ? HaScorecard(entity) : null,
            ClusterFailover = entity.Kind == EntityKind.Cluster ? ClusterFailover(entity, graph) : null,
        };
    }

    /// <summary>
    /// The HA scorecard, read from the same <c>Entity.Settings</c> the
    /// collector filed under <c>ClusterHighAvailabilityPolicy</c>'s keys and
    /// the cluster's <c>eo-cont.ha-*</c> continuity findings.
    /// </summary>
    /// <remarks>
    /// Filtered, never recomputed -- ADR-0007 §1. The setting keys are read
    /// from the rule's own default policy rather than restated here, so the
    /// two can never drift apart: see <see cref="ClusterHighAvailabilityPolicy"/>.
    /// </remarks>
    private HaScorecardView HaScorecard(Entity cluster)
    {
        var rules = ClusterHighAvailabilityPolicy.Default;
        var settings = cluster.Settings;

        return new HaScorecardView
        {
            Enabled = Bool(settings, rules.EnabledSetting),
            AdmissionControlEnabled = Bool(settings, rules.AdmissionControlEnabledSetting),
            // Not in ClusterHighAvailabilityPolicy: no HA rule check reads
            // this key, only the scorecard displays it, so the policy (which
            // exists to keep a rule and this display from drifting apart) has
            // no property for it. Pinned against the collector's own constant
            // by AdmissionControlPolicyTypeAndVmMonitoringSettingsDriftTests
            // in Host.AllInOne.Tests instead.
            AdmissionControlPolicyType = settings.GetValueOrDefault("dasConfig.admissionControlPolicy.type"),
            HostMonitoring = settings.GetValueOrDefault(rules.HostMonitoringSetting),
            // Same as AdmissionControlPolicyType above: display-only, no rule
            // reads it, so it is not on the policy.
            VmMonitoring = settings.GetValueOrDefault("dasConfig.vmMonitoring"),
            ApdResponse = settings.GetValueOrDefault(rules.ApdResponseSetting),
            PdlResponse = settings.GetValueOrDefault(rules.PdlResponseSetting),
            HeartbeatDatastoreCount =
                settings.TryGetValue(rules.HeartbeatDatastoreCountSetting, out var count) &&
                int.TryParse(count, out var parsed)
                    ? parsed
                    : null,
            HeartbeatDatastoreCandidatePolicy =
                settings.GetValueOrDefault(rules.HeartbeatDatastoreCandidatePolicySetting),
            RedundantNetworkWarningSilenced = Bool(settings, rules.IgnoreRedundantNetworkWarningSetting),
            // The cluster's eo-cont.ha-* findings in every state, from the
            // same store the compliance screen reads (ADR-0024).
            Findings = HaFindings(cluster.Id),
        };
    }

    /// <summary>The cluster's HA continuity findings, in catalogue order.</summary>
    private IReadOnlyList<ContinuityFindingView> HaFindings(EntityId cluster)
    {
        var (findings, exceptions) = Continuity();
        var now = _clock.UtcNow;
        var order = _continuityChecks.Select(c => c.Control.ControlId).ToList();

        return
        [
            .. findings
                .Where(f => f.Entity == cluster && IsHa(f.ControlId))
                .OrderBy(f => order.IndexOf(f.ControlId))
                .Select(f => ToView(f, exceptions, now)),
        ];
    }

    private static bool? Bool(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed) ? parsed : null;

    /// <summary>
    /// The same estimate the fill-date rule makes, from the same history.
    /// </summary>
    /// <remarks>
    /// Computed on request from the stored series rather than remembered from
    /// the last cycle: the rule keeps nothing, and a copy of its answer would
    /// be a second model. The capacity is the latest one recorded, where the
    /// rule has the reading of the cycle it runs in; the two agree whenever
    /// the capacity has not just changed.
    /// </remarks>
    private TimeToFullView TimeToFull(EntityId datastore)
    {
        var now = _clock.UtcNow;
        var retention = _options.Retention;

        if (DatastoreTimeToFull.LatestCapacity(_observations, datastore, now, retention) is not { } capacity)
        {
            return new TimeToFullView
            {
                IsForecast = false,
                PointsUsed = 0,
                Reason = "NoCapacity",
                Summary =
                    $"Cannot estimate a fill date: no capacity reading was recorded in the last " +
                    $"{retention.Raw.TotalDays:0} days.",
            };
        }

        var estimate = DatastoreTimeToFull.Read(
            _observations, datastore, capacity, now, _options.DatastoreTimeToFull, retention);

        var summary = DatastoreTimeToFull.Explain(estimate);
        summary = char.ToUpperInvariant(summary[0]) + summary[1..] + (summary.EndsWith('.') ? "" : ".");

        return estimate switch
        {
            TimeToFullResult.Forecast f => new TimeToFullView
            {
                IsForecast = true,
                FullAtUtc = f.FullAtUtc,
                Days = f.Days,
                GrowthBytesPerDay = f.SlopePerDay,
                WindowFromUtc = f.Window.FromUtc,
                WindowToUtc = f.Window.ToUtc,
                PointsUsed = f.PointsUsed,
                Summary = summary,
            },
            TimeToFullResult.Refusal r => new TimeToFullView
            {
                IsForecast = false,
                GrowthBytesPerDay = r.SlopePerDay,
                WindowFromUtc = r.Window?.FromUtc,
                WindowToUtc = r.Window?.ToUtc,
                PointsUsed = r.PointsUsed,
                Reason = r.Reason.ToString(),
                Summary = summary,
            },
            _ => throw new InvalidOperationException("An estimate is a forecast or a refusal."),
        };
    }

    /// <summary>
    /// The same N+1 verdict the rule reaches, computed on request from the
    /// series store's latest samples and history rather than remembered from
    /// the last cycle -- the same split <see cref="TimeToFull(EntityId)"/>
    /// makes for a datastore. Null when this cluster has fewer than two live
    /// hosts, the case <see cref="ClusterNPlusOne.HostsByLiveCluster"/> leaves
    /// out entirely.
    /// </summary>
    private ClusterFailoverView? ClusterFailover(Entity cluster, EntityGraph graph)
    {
        var now = _clock.UtcNow;
        var retention = _options.Retention;
        var policy = _options.ClusterNPlusOne;

        var state = ClusterNPlusOne
            .CurrentReadings(_observations, graph, now, policy, retention)
            .SingleOrDefault(s => s.Cluster == cluster.Id);

        if (state is null)
        {
            return null;
        }

        var available = ClusterNPlusOne.AvailableAfterFailoverHosts(state.HostCount, policy);

        return new ClusterFailoverView
        {
            HostCount = state.HostCount,
            Cpu = ClusterFailoverResource(
                state, ClusterCapacityResource.Cpu, state.CpuDemandHosts, policy.CpuUsageCounter,
                available, now, policy, retention),
            Memory = ClusterFailoverResource(
                state, ClusterCapacityResource.Memory, state.MemoryDemandHosts, policy.MemoryUsageCounter,
                available, now, policy, retention),
        };
    }

    private ClusterFailoverResourceView ClusterFailoverResource(
        ClusterFailoverState state,
        ClusterCapacityResource resource,
        double? demandHosts,
        string counter,
        double availableAfterFailoverHosts,
        DateTimeOffset now,
        ClusterNPlusOnePolicy policy,
        SeriesRetentionPolicy retention)
    {
        if (demandHosts is not { } demand)
        {
            return new ClusterFailoverResourceView { AvailableAfterFailoverHosts = availableAfterFailoverHosts };
        }

        var estimate = ClusterNPlusOne.ReadDate(
            _observations, state.Cluster, state.Hosts, counter, availableAfterFailoverHosts,
            resource, now, policy, retention);

        var summary = ClusterNPlusOne.Explain(estimate, resource);
        summary = char.ToUpperInvariant(summary[0]) + summary[1..] + (summary.EndsWith('.') ? "" : ".");

        var date = estimate switch
        {
            TimeToFullResult.Forecast f => new TimeToFullView
            {
                IsForecast = true,
                FullAtUtc = f.FullAtUtc,
                Days = f.Days,
                GrowthBytesPerDay = f.SlopePerDay,
                WindowFromUtc = f.Window.FromUtc,
                WindowToUtc = f.Window.ToUtc,
                PointsUsed = f.PointsUsed,
                Summary = summary,
            },
            TimeToFullResult.Refusal r => new TimeToFullView
            {
                IsForecast = false,
                GrowthBytesPerDay = r.SlopePerDay,
                WindowFromUtc = r.Window?.FromUtc,
                WindowToUtc = r.Window?.ToUtc,
                PointsUsed = r.PointsUsed,
                Reason = r.Reason.ToString(),
                Summary = summary,
            },
            _ => throw new InvalidOperationException("An estimate is a forecast or a refusal."),
        };

        return new ClusterFailoverResourceView
        {
            HoldsNow = demand <= availableAfterFailoverHosts + 1e-9,
            DemandHosts = demand,
            AvailableAfterFailoverHosts = availableAfterFailoverHosts,
            Date = date,
        };
    }

    // --- collectors -------------------------------------------------------

    /// <summary>
    /// The monitoring system observing itself.
    /// </summary>
    /// <remarks>
    /// A first-class screen rather than a log file. "Is the monitoring working"
    /// must be answerable from inside the product; in the previous one it was
    /// answerable only by reading logs on the server. See ADR-0005.
    /// </remarks>
    /// <summary>
    /// What each source could read, worst first.
    /// </summary>
    /// <remarks>
    /// Ordered so the blind rows are at the top and the complete ones at the
    /// bottom, because a report of forty rows in which two matter is read by
    /// nobody if the two are in the middle. Complete rows are kept rather than
    /// filtered out: "this was checked and it is fine" is the half that makes
    /// the other half trustworthy, and a screen that only ever shows problems
    /// cannot distinguish a healthy estate from a report that stopped running.
    /// </remarks>
    public IReadOnlyList<CoverageView> Coverage() =>
    [
        .. _coverage.Current
            .OrderBy(c => c.SourceInstanceId, StringComparer.Ordinal)
            .Select(c => new CoverageView
            {
                InstanceId = c.SourceInstanceId,
                MeasuredAtUtc = c.MeasuredAtUtc,
                Properties =
                [
                    .. c.Properties
                        .OrderByDescending(p => p.IsBlind)
                        .ThenBy(p => p.Answered - p.Asked)
                        .ThenBy(p => p.ObjectType, StringComparer.Ordinal)
                        .ThenBy(p => p.Property, StringComparer.Ordinal)
                        .Select(p => new CoveragePropertyView
                        {
                            ObjectType = p.ObjectType,
                            Property = p.Property,
                            Asked = p.Asked,
                            Answered = p.Answered,
                        }),
                ],
            }),
    ];

    public IReadOnlyList<CollectorView> Collectors() =>
    [
        .. _collectors.Current
            .OrderBy(c => c.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Role)
            .Select(c => new CollectorView
            {
                InstanceId = c.InstanceId,
                Role = c.Role.ToString(),
                Health = c.Health,
                ConsecutiveFailures = c.ConsecutiveFailures,
                IsBackingOff = c.IsBackingOff,
                LastSuccessUtc = c.LastSuccessUtc,
                LastFailureDetail = c.LastFailureDetail,
                PartialFailures = [.. c.PartialFailures.Select(f => new PartialFailureView
                {
                    Kind = f.Kind.ToString(),
                    Target = f.Target,
                    Detail = f.Detail,
                })],
            }),
    ];

    // --- events -------------------------------------------------------------

    /// <summary>
    /// The inbox, grouped into what is actually going wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A projection over the same instances the flat list shows, computed on
    /// demand rather than stored. Correlation depends on the graph, and the
    /// graph changes; a stored grouping would be a claim about a topology that
    /// no longer exists.
    /// </para>
    /// <para>
    /// Every visible alert appears exactly once, in an event or on its own, and
    /// the total is reported so the arithmetic can be checked. See ADR-0007.
    /// </para>
    /// </remarks>
    public EventBoardView Events(CorrelationPolicy? policy = null)
    {
        var graph = _graphs.Current;
        var visible = Visible();
        var byFingerprint = visible.ToDictionary(a => a.Fingerprint);

        var result = EventCorrelator.Correlate(visible, graph, policy ?? CorrelationPolicy.Default);

        AlertView Look(AlertFingerprint fingerprint) => ToView(byFingerprint[fingerprint], graph);

        return new EventBoardView
        {
            Events =
            [
                .. result.Events.Select(e => new EventView
                {
                    Id = e.Id,
                    Title = e.Title,
                    Severity = e.Severity,
                    RootId = e.Root.Value,
                    RootName = NameOf(graph, e.Root),
                    AlertCount = e.Alerts.Count,
                    EntityCount = e.Entities.Count,
                    FirstSeenUtc = e.FirstSeenUtc,
                    LastSeenUtc = e.LastSeenUtc,
                    Alerts = [.. e.Alerts.Select(Look)],
                    Explanation =
                    [
                        .. e.Explanation.Select(link => new CorrelationLinkView
                        {
                            FromId = link.From.Value,
                            FromName = NameOf(graph, link.From),
                            Kind = link.Kind,
                            ToId = link.To.Value,
                            ToName = NameOf(graph, link.To),
                        }),
                    ],
                }),
            ],
            Suggestions =
            [
                .. result.Suggestions.Select(s => new SuggestionView
                {
                    WithinUtc = s.WithinUtc,
                    WindowSeconds = (int)s.Window.TotalSeconds,
                    Alerts = [.. s.Alerts.Select(Look)],
                }),
            ],
            Ungrouped =
            [
                .. result.Ungrouped
                    .Select(Look)
                    .OrderByDescending(a => a.Severity)
                    .ThenByDescending(a => a.LastSeenUtc),
            ],
            TotalAlerts = visible.Count,
        };
    }

    /// <summary>An entity display name, or its id when it is not in the graph.</summary>
    /// <remarks>
    /// An identifier is a poor thing to show an operator, but it beats an empty
    /// space: it at least says which thing the product means.
    /// </remarks>
    private static string NameOf(EntityGraph graph, EntityId id) =>
        graph.Entities.TryGetValue(id, out var entity) ? entity.DisplayName : id.Value;

    // --- measurements -----------------------------------------------------

    /// <summary>Which counters exist for an entity.</summary>
    /// <remarks>
    /// Read from what has actually been recorded rather than from a fixed menu
    /// per entity type. A menu would be wrong for half of them and would go on
    /// offering a counter the platform stopped providing.
    /// </remarks>
    public IReadOnlyList<SeriesOptionView> SeriesFor(string entityId) =>
    [
        .. _observations.SeriesFor(new EntityId(entityId))
            .Select(k => new SeriesOptionView { Counter = k.Counter, Instance = k.Instance }),
    ];

    /// <summary>One series over a window.</summary>
    public SeriesView Series(
        string entityId,
        string counter,
        string? instance,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int maxPoints)
    {
        var to = toUtc ?? _clock.UtcNow;
        var from = fromUtc ?? to.AddHours(-1);

        var budget = Math.Clamp(maxPoints, 1, 2_000);

        // Named explicitly rather than left to the store's width-only rule.
        // That rule picks the finest tier the budget allows and never asks
        // whether the tier still reaches back that far, so an hour-wide window
        // from last week chose raw, raw had been compacted away, and the answer
        // came back as an existing series with no points — which reads on
        // screen as a collector problem.
        var resolution = _options.Retention.RetainedResolutionFor(
            to - from, budget, _clock.UtcNow - from);

        var result = _observations.Query(new SeriesQuery
        {
            Key = new SeriesKey(new EntityId(entityId), counter, instance ?? string.Empty),
            FromUtc = from,
            ToUtc = to,
            MaxPoints = budget,
            Resolution = resolution,
        });

        return new SeriesView
        {
            EntityId = entityId,
            Counter = counter,
            Instance = instance ?? string.Empty,
            Exists = result.Exists,
            Resolution = result.Resolution,
            Unit = result.Unit,
            Rollup = result.Rollup,
            Truncated = result.Truncated,
            Points =
            [
                .. result.Points.Select(p => new SeriesPointView
                {
                    AtUtc = p.StartUtc,
                    Min = p.Min,
                    Max = p.Max,
                    Average = p.Average,
                    Last = p.Last,
                    Count = p.Count,
                }),
            ],
        };
    }

    // --- internals --------------------------------------------------------

    private IReadOnlyList<AlertInstance> Visible() => [.. _alerts.All.Where(a => a.IsVisible)];

    private Dictionary<EntityId, int> AlertCountsByEntity() =>
        Visible()
            .Where(a => a.Entity is not null)
            .GroupBy(a => a.Entity!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

    private static bool Matches(AlertInstance alert, string? search) =>
        search is null ||
        alert.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        alert.Description.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static Page<TView> Paged<TSource, TView>(
        IReadOnlyList<TSource> all,
        int offset,
        int limit,
        Func<TSource, TView> project)
    {
        var safeLimit = Math.Clamp(limit, 1, MaxLimit);
        var safeOffset = Math.Max(offset, 0);

        return new Page<TView>
        {
            Items = [.. all.Skip(safeOffset).Take(safeLimit).Select(project)],
            Total = all.Count,
            Offset = safeOffset,
            Limit = safeLimit,
        };
    }

    /// <summary>Presents one alert instance, for a command's response.</summary>
    public AlertView Present(AlertInstance alert) => ToView(alert, _graphs.Current);

    private static AlertView ToView(AlertInstance alert, EntityGraph graph)
    {
        string? name = null;

        if (alert.Entity is { } entityId && graph.Entities.TryGetValue(entityId, out var entity))
        {
            name = entity.DisplayName;
        }

        return new AlertView
        {
            Fingerprint = alert.Fingerprint.Value,
            Severity = alert.Severity,
            State = alert.State,
            Title = alert.Title,
            Description = alert.Description,
            Category = alert.Category,
            Source = alert.Source,
            EntityId = alert.Entity?.Value,
            EntityName = name,
            IsDerived = alert.IsDerived,
            SuppressedByWindowId = alert.SuppressedByWindowId,
            FirstSeenUtc = alert.FirstSeenUtc,
            LastSeenUtc = alert.LastSeenUtc,
            EvidenceAtUtc = alert.EvidenceAtUtc,
            IsStale = alert.IsStale,
            StaleSinceUtc = alert.StaleSinceUtc,
            StaleReason = alert.StaleReason,
            StaleDetail = alert.StaleDetail,
        };
    }

    private static EntityView ToView(Entity entity, Dictionary<EntityId, int> alertCounts, DerivedHealth health) => new()
    {
        Id = entity.Id.Value,
        Kind = entity.Kind,
        DisplayName = entity.DisplayName,
        Health = health.Health,
        HealthBasis = health.Basis,
        HealthIsStale = health.IsStale,
        HealthStaleSinceUtc = health.StaleSinceUtc,
        ObservationState = entity.ObservationState,
        Source = entity.SourceInstanceId,
        LastSeenUtc = entity.LastSeenUtc,
        AlertCount = alertCounts.GetValueOrDefault(entity.Id),
    };

    /// <summary>
    /// Every edge touching one entity, in both directions.
    /// </summary>
    /// <remarks>
    /// A linear scan of the edge list. Fine at the size of one estate's
    /// topology and wrong at ten; it will need an adjacency index before it
    /// becomes a problem. Noted rather than solved, because the shape of the
    /// index depends on which traversals turn out to matter and we have not
    /// measured that yet.
    /// </remarks>
    private static List<RelationshipView> RelationshipsOf(
        EntityGraph graph,
        EntityId id,
        Dictionary<EntityId, DerivedHealth> health)
    {
        var views = new List<RelationshipView>();

        foreach (var edge in graph.Relationships)
        {
            var outgoing = edge.From == id;

            if (!outgoing && edge.To != id)
            {
                continue;
            }

            var otherId = outgoing ? edge.To : edge.From;

            if (!graph.Entities.TryGetValue(otherId, out var other))
            {
                continue;
            }

            views.Add(new RelationshipView
            {
                Kind = edge.Kind,
                IsOutgoing = outgoing,
                OtherId = otherId.Value,
                OtherName = other.DisplayName,
                OtherKind = other.Kind,
                OtherHealth = health[otherId].Health,
            });
        }

        return views;
    }
}
