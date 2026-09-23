using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Monitoring;

/// <summary>Three-valued verdict about the service itself, for <c>/health</c>.</summary>
/// <remarks>
/// Not <see cref="HealthState"/>: that one is about an entity in the estate,
/// judged from what a collector read. This one is about whether the product
/// is still reading anything at all, judged from the age of the last read —
/// the question docs/live-verification.md §9 found the product could not
/// answer during a four-hour outage.
/// </remarks>
public enum ServiceHealthStatus
{
    Healthy,
    Degraded,
    Unhealthy,
}

/// <summary>How stale a role's cycles are allowed to get before <c>/health</c> says so.</summary>
/// <remarks>
/// Two independent thresholds because they answer different questions: cycles
/// running late are merely behind, worth a warning an external monitor should
/// not page on; no cycle at all for fifteen minutes means the product has
/// stopped, and paging on that is the whole point of this endpoint existing.
/// </remarks>
public sealed record HealthOptions
{
    /// <summary>
    /// A role is <see cref="ServiceHealthStatus.Degraded"/> once its most
    /// recent attempt is older than this many of its own intervals.
    /// </summary>
    public int DegradedAfterIntervals { get; init; } = 3;

    /// <summary>
    /// A role is <see cref="ServiceHealthStatus.Unhealthy"/> once its most
    /// recent attempt is at least this old, whatever the interval.
    /// </summary>
    /// <remarks>
    /// Configurable per the roadmap: an installation with a five-minute
    /// inventory interval and one with a thirty-second one should not have to
    /// share the same "the operator must be paged now" line.
    /// </remarks>
    public TimeSpan UnhealthyAfter { get; init; } = TimeSpan.FromMinutes(15);

    public static HealthOptions Default { get; } = new();
}

/// <summary>One source's own state, as <c>/health</c> lists it beside the verdict.</summary>
public enum SourceStatus
{
    Healthy,
    Warning,

    /// <summary>Polled, but the last attempt did not read it (unreachable, refused, ...).</summary>
    Unknown,

    /// <summary>
    /// No collector exists for this source's kind
    /// (<see cref="CollectionFailureKind.NotConfigured"/>): nothing is polled,
    /// so there is nothing unknown about it either.
    /// </summary>
    NotPolled,
}

/// <summary>One source, as <c>/health</c> lists it.</summary>
/// <remarks>
/// Visible, but not part of the verdict: see <see cref="HealthAssessment"/>.
/// </remarks>
public sealed record SourceFreshness
{
    public required string InstanceId { get; init; }

    public required CollectorRole Role { get; init; }

    public required SourceStatus Status { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    public DateTimeOffset? LastAttemptUtc { get; init; }
}

/// <summary>One role's freshness, as <c>/health</c> reports it.</summary>
public sealed record RoleFreshness
{
    public required CollectorRole Role { get; init; }

    /// <summary>
    /// The oldest "last successful read" among this role's polled sources.
    /// Informational: a customer's vCenter being unreachable shows here and in
    /// <see cref="HealthReport.Sources"/>, not in <see cref="Status"/>.
    /// </summary>
    public DateTimeOffset? OldestLastSuccessUtc { get; init; }

    /// <summary>
    /// The freshest attempt among this role's polled sources — what
    /// <see cref="Status"/> is judged from.
    /// </summary>
    public DateTimeOffset? FreshestAttemptUtc { get; init; }

    /// <summary>Age of <see cref="FreshestAttemptUtc"/>; null when nothing was ever attempted.</summary>
    public TimeSpan? Age { get; init; }

    public required ServiceHealthStatus Status { get; init; }
}

/// <summary>The whole answer to "is the monitoring system itself working".</summary>
public sealed record HealthReport
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required ServiceHealthStatus Status { get; init; }

    public IReadOnlyList<RoleFreshness> Roles { get; init; } = [];

    /// <summary>Every source's own state, polled or not. Not the verdict.</summary>
    public IReadOnlyList<SourceFreshness> Sources { get; init; } = [];

    /// <summary>False when the store queue's last write attempt failed.</summary>
    public bool StoreReachable { get; init; } = true;

    /// <summary>Why the last store write failed; null while the store is taking writes.</summary>
    public string? StoreFailure { get; init; }

    /// <summary>Gaps still being filled, across every source.</summary>
    public int OpenGaps { get; init; }

    /// <summary>
    /// Gaps closed with part of their history permanently lost, across every
    /// source and all of history — <see cref="ICollectionGapStore"/> never
    /// deletes a row.
    /// </summary>
    public int UnrecoverableGaps { get; init; }
}

/// <summary>
/// Judges the product's own health from what is already recorded — no new
/// collection, no new write, per the roadmap's Package D redefinition
/// (docs/feature-roadmap.md "D — Öz-izleme"): a health endpoint surfaces
/// <see cref="ICollectorHealthStore"/>, <see cref="ICollectionGapStore"/> and
/// the store queue's last write, it does not invent a probe of its own.
/// </summary>
/// <remarks>
/// <para>
/// The product's health, not the union of its sources' (decided 23 September
/// 2026, reversing "the worst source decides"). Prometheus draws the same
/// line: a target's <c>up</c> is not the server's own readiness, which it
/// reports separately; Kubernetes separates liveness from what a pod depends
/// on. A customer's vCenter being unreachable is the product working
/// correctly — it already raises its own "Collector unreachable" alert — and
/// judging /health by it left /health at 503 permanently on the live estate,
/// so it could no longer page on the one thing it exists for: the process
/// itself having stopped (docs/live-verification.md §9).
/// </para>
/// <para>
/// So: Healthy while every role's cycles are running — the freshest attempt
/// among its polled sources within the thresholds — and the store is taking
/// writes. Unhealthy only when a role has attempted nothing within
/// <see cref="HealthOptions.UnhealthyAfter"/> or the store's last write
/// failed. Sources whose kind has no collector
/// (<see cref="CollectionFailureKind.NotConfigured"/>) are not polled and
/// count toward no role.
/// </para>
/// </remarks>
public static class HealthAssessment
{
    /// <param name="storeFailure">
    /// The store queue's last write failure (<see cref="StoreQueueSnapshot.LastFailure"/>);
    /// null when the store is taking writes or no queue exists.
    /// </param>
    public static HealthReport Assess(
        IReadOnlyList<CollectorHealth> health,
        MonitoringOptions monitoring,
        HealthOptions healthOptions,
        IReadOnlyDictionary<CollectionGapState, int> gapCounts,
        DateTimeOffset nowUtc,
        string? storeFailure = null,
        IReadOnlySet<string>? disabledConnections = null)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(monitoring);
        ArgumentNullException.ThrowIfNull(healthOptions);
        ArgumentNullException.ThrowIfNull(gapCounts);

        // A connection an operator switched off is as unpolled as one with no
        // collector for its kind: neither says anything about the product.
        bool NotPolled(CollectorHealth h) =>
            h.LastFailureKind == CollectionFailureKind.NotConfigured ||
            disabledConnections?.Contains(h.InstanceId) == true;

        var polled = health.Where(h => !NotPolled(h)).ToList();

        var roles = new List<RoleFreshness>
        {
            AssessRole(CollectorRole.Inventory, polled, monitoring.InventoryInterval, healthOptions, nowUtc),
            AssessRole(CollectorRole.Observation, polled, monitoring.ObservationInterval, healthOptions, nowUtc),

            // Events (F1, CollectorRole.Events) reads on the inventory
            // cadence — it runs right after the inventory cycle, on the same
            // vCenters that just answered — so its own collector_health row
            // is judged against that interval too.
            AssessRole(CollectorRole.Events, polled, monitoring.InventoryInterval, healthOptions, nowUtc),
            AssessRole(CollectorRole.Configuration, polled, monitoring.ConfigurationInterval, healthOptions, nowUtc),
        };

        // No polled sources of a role reports that role Healthy (nothing to
        // be unhealthy about — the same reading NoteWhetherAnythingIsConfigured
        // gives an empty installation elsewhere in the product). Otherwise the
        // worst role wins: a role whose cycles stopped is the product stopped.
        var overall = roles.Select(r => r.Status).DefaultIfEmpty(ServiceHealthStatus.Healthy).Max();

        if (storeFailure is not null)
        {
            overall = ServiceHealthStatus.Unhealthy;
        }

        return new HealthReport
        {
            GeneratedAtUtc = nowUtc,
            Status = overall,
            Roles = roles,
            Sources = [.. health.Select(h => ToSource(h, NotPolled(h)))],
            StoreReachable = storeFailure is null,
            StoreFailure = storeFailure,
            OpenGaps = gapCounts.GetValueOrDefault(CollectionGapState.Open),
            UnrecoverableGaps = gapCounts.GetValueOrDefault(CollectionGapState.Unrecoverable),
        };
    }

    private static SourceFreshness ToSource(CollectorHealth h, bool notPolled) => new()
    {
        InstanceId = h.InstanceId,
        Role = h.Role,
        Status = notPolled
            ? SourceStatus.NotPolled
            : h.Health switch
            {
                HealthState.Healthy => SourceStatus.Healthy,
                HealthState.Warning => SourceStatus.Warning,
                _ => SourceStatus.Unknown,
            },
        LastSuccessUtc = h.LastSuccessUtc,
        LastAttemptUtc = h.LastAttemptUtc,
    };

    private static RoleFreshness AssessRole(
        CollectorRole role,
        IReadOnlyList<CollectorHealth> polled,
        TimeSpan interval,
        HealthOptions healthOptions,
        DateTimeOffset nowUtc)
    {
        var ofRole = polled.Where(h => h.Role == role).ToList();

        if (ofRole.Count == 0)
        {
            return new RoleFreshness { Role = role, Status = ServiceHealthStatus.Healthy };
        }

        var oldestSuccess = ofRole.Min(h => h.LastSuccessUtc);

        // The freshest attempt decides, not the oldest success: the question
        // is whether this role's cycles still run, and one source answering —
        // or failing to, which is still a cycle — says they do. A row from
        // before LastAttemptUtc existed falls back to its last success.
        var freshest = ofRole.Max(h => h.LastAttemptUtc ?? h.LastSuccessUtc);

        if (freshest is null)
        {
            // Polled sources, none ever attempted: no evidence any cycle ran.
            return new RoleFreshness
            {
                Role = role,
                OldestLastSuccessUtc = oldestSuccess,
                Status = ServiceHealthStatus.Unhealthy,
            };
        }

        var age = nowUtc - freshest.Value;

        var degradedAfter = interval * Math.Max(1, healthOptions.DegradedAfterIntervals);

        var status = age >= healthOptions.UnhealthyAfter
            ? ServiceHealthStatus.Unhealthy
            : age >= degradedAfter
                ? ServiceHealthStatus.Degraded
                : ServiceHealthStatus.Healthy;

        return new RoleFreshness
        {
            Role = role,
            OldestLastSuccessUtc = oldestSuccess,
            FreshestAttemptUtc = freshest,
            Age = age,
            Status = status,
        };
    }
}
