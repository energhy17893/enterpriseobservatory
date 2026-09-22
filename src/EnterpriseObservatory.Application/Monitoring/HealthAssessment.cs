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

/// <summary>How stale a read is allowed to get before <c>/health</c> says so.</summary>
/// <remarks>
/// Two independent thresholds because they answer different questions: a
/// screen showing five-minute-old inventory is merely behind, worth a warning
/// an external monitor should not page on; a screen showing nothing for fifteen
/// minutes has stopped, and paging on that is the whole point of this
/// endpoint existing.
/// </remarks>
public sealed record HealthOptions
{
    /// <summary>
    /// A role is <see cref="ServiceHealthStatus.Degraded"/> once its last
    /// successful read is older than this many of its own intervals.
    /// </summary>
    public int DegradedAfterIntervals { get; init; } = 3;

    /// <summary>
    /// A role is <see cref="ServiceHealthStatus.Unhealthy"/> once its last
    /// successful read is at least this old, whatever the interval.
    /// </summary>
    /// <remarks>
    /// Configurable per the roadmap: an installation with a five-minute
    /// inventory interval and one with a thirty-second one should not have to
    /// share the same "the operator must be paged now" line.
    /// </remarks>
    public TimeSpan UnhealthyAfter { get; init; } = TimeSpan.FromMinutes(15);

    public static HealthOptions Default { get; } = new();
}

/// <summary>One role's freshness, as <c>/health</c> reports it.</summary>
public sealed record RoleFreshness
{
    public required CollectorRole Role { get; init; }

    /// <summary>
    /// The oldest "last successful read" among this role's configured
    /// sources — the worst case, because one silent source is one blind spot
    /// whatever the others are doing.
    /// </summary>
    public DateTimeOffset? OldestLastSuccessUtc { get; init; }

    /// <summary>Null when no source of this role has ever read successfully.</summary>
    public TimeSpan? Age { get; init; }

    public required ServiceHealthStatus Status { get; init; }
}

/// <summary>The whole answer to "is the monitoring system itself working".</summary>
public sealed record HealthReport
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required ServiceHealthStatus Status { get; init; }

    public IReadOnlyList<RoleFreshness> Roles { get; init; } = [];

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
/// Judges the product's own freshness from what is already recorded — no new
/// collection, no new write, per the roadmap's Package D redefinition
/// (docs/feature-roadmap.md "D — Öz-izleme"): a health endpoint surfaces
/// <see cref="ICollectorHealthStore"/> and <see cref="ICollectionGapStore"/>,
/// it does not invent a probe of its own.
/// </summary>
public static class HealthAssessment
{
    public static HealthReport Assess(
        IReadOnlyList<CollectorHealth> health,
        MonitoringOptions monitoring,
        HealthOptions healthOptions,
        IReadOnlyDictionary<CollectionGapState, int> gapCounts,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(monitoring);
        ArgumentNullException.ThrowIfNull(healthOptions);
        ArgumentNullException.ThrowIfNull(gapCounts);

        var roles = new List<RoleFreshness>
        {
            AssessRole(CollectorRole.Inventory, health, monitoring.InventoryInterval, healthOptions, nowUtc),
            AssessRole(CollectorRole.Observation, health, monitoring.ObservationInterval, healthOptions, nowUtc),

            // Events (F1, CollectorRole.Events) reads on the inventory
            // cadence — it runs right after the inventory cycle, on the same
            // vCenters that just answered — so its own collector_health row
            // is judged against that interval too.
            AssessRole(CollectorRole.Events, health, monitoring.InventoryInterval, healthOptions, nowUtc),
        };

        // No sources of a role configured reports that role Healthy (nothing
        // to be unhealthy about — the same reading
        // NoteWhetherAnythingIsConfigured gives an empty installation
        // elsewhere in the product), so it never worsens the overall verdict.
        // Otherwise the worst role wins: one blind role makes the whole
        // product's self-report say so, because an operator reading
        // "Healthy" must be able to trust that every role behind it agrees.
        var overall = roles.Select(r => r.Status).DefaultIfEmpty(ServiceHealthStatus.Healthy).Max();

        return new HealthReport
        {
            GeneratedAtUtc = nowUtc,
            Status = overall,
            Roles = roles,
            OpenGaps = gapCounts.GetValueOrDefault(CollectionGapState.Open),
            UnrecoverableGaps = gapCounts.GetValueOrDefault(CollectionGapState.Unrecoverable),
        };
    }

    private static RoleFreshness AssessRole(
        CollectorRole role,
        IReadOnlyList<CollectorHealth> health,
        TimeSpan interval,
        HealthOptions healthOptions,
        DateTimeOffset nowUtc)
    {
        var ofRole = health.Where(h => h.Role == role).ToList();

        if (ofRole.Count == 0)
        {
            return new RoleFreshness { Role = role, Status = ServiceHealthStatus.Healthy };
        }

        // The worst source decides, not the average: one vCenter unreachable
        // for four hours must not be hidden behind nine that are fine.
        var everSucceeded = ofRole.Where(h => h.LastSuccessUtc is not null).ToList();

        if (everSucceeded.Count < ofRole.Count)
        {
            // At least one configured source has never once read
            // successfully. Nothing to measure an age from, and nothing
            // healthy about it either.
            return new RoleFreshness { Role = role, Status = ServiceHealthStatus.Unhealthy };
        }

        var oldest = everSucceeded.Min(h => h.LastSuccessUtc!.Value);
        var age = nowUtc - oldest;

        var degradedAfter = interval * Math.Max(1, healthOptions.DegradedAfterIntervals);

        var status = age >= healthOptions.UnhealthyAfter
            ? ServiceHealthStatus.Unhealthy
            : age >= degradedAfter
                ? ServiceHealthStatus.Degraded
                : ServiceHealthStatus.Healthy;

        return new RoleFreshness
        {
            Role = role,
            OldestLastSuccessUtc = oldest,
            Age = age,
            Status = status,
        };
    }
}
