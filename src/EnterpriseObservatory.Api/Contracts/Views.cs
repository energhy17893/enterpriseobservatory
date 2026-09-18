using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Api.Contracts;

/// <summary>
/// A page of results.
/// </summary>
/// <remarks>
/// Paged rather than complete because an estate has thousands of virtual
/// machines and the previous product rendered every row into the DOM. The total
/// is carried alongside so the client can say "1–50 of 2,184" rather than
/// discovering the end by running out.
/// </remarks>
public sealed record Page<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required int Total { get; init; }

    public required int Offset { get; init; }

    public required int Limit { get; init; }
}

/// <summary>
/// An alert as the inbox shows it.
/// </summary>
/// <remarks>
/// Projected from the one set of lifecycle instances. Every surface — the
/// inbox, an entity page, a vendor deep view — reads this same projection, so
/// an alert cannot appear acknowledged on one screen and open on another. That
/// was a real defect in the previous product and is what ADR-0007 exists to
/// prevent.
/// </remarks>
public sealed record AlertView
{
    public required string Fingerprint { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required AlertLifecycleState State { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    /// <summary>What kind of problem this is, e.g. Hardware or Configuration.</summary>
    public required string Category { get; init; }

    /// <summary>Which collector or subsystem reported it.</summary>
    /// <remarks>
    /// The filter axis vendor pages are built on. ADR-0007 keeps the vendor out
    /// of the top-level navigation and keeps it here, where it belongs: a way
    /// of narrowing one model rather than a separate model.
    /// </remarks>
    public required string Source { get; init; }

    /// <summary>The entity this is about, when it resolves to one.</summary>
    public string? EntityId { get; init; }

    /// <summary>
    /// That entity's display name, resolved here rather than by the client.
    /// </summary>
    /// <remarks>
    /// A client that resolved names itself would need the whole graph to render
    /// a list of ten alerts, and would show a raw identifier whenever it did
    /// not have it.
    /// </remarks>
    public string? EntityName { get; init; }

    /// <summary>Whether the platform inferred this rather than observing it.</summary>
    public required bool IsDerived { get; init; }

    /// <summary>
    /// Set when a maintenance window is suppressing notification.
    /// </summary>
    /// <remarks>
    /// The alert is still shown. Maintenance stops the paging, not the
    /// reporting: hiding it would mean an operator working in a window cannot
    /// see what they are doing.
    /// </remarks>
    public string? SuppressedByWindowId { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }
}

/// <summary>An entity as the explorer lists it (tier 1 of ADR-0007).</summary>
public sealed record EntityView
{
    public required string Id { get; init; }

    public required EntityKind Kind { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// Health as it should be reported, not as last observed.
    /// </summary>
    /// <remarks>
    /// An entity we cannot currently see is Unknown whatever colour it was when
    /// we last saw it. See <see cref="Entity.EffectiveHealth"/> and product
    /// principle 1.
    /// </remarks>
    public required HealthState Health { get; init; }

    public required ObservationState ObservationState { get; init; }

    /// <summary>Which collector reported it.</summary>
    public required string Source { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>Visible alerts currently scoped to this entity.</summary>
    public required int AlertCount { get; init; }
}

/// <summary>One edge, from the point of view of the entity being looked at.</summary>
public sealed record RelationshipView
{
    public required RelationshipKind Kind { get; init; }

    /// <summary>
    /// Whether this entity is the subject or the object of the relationship.
    /// </summary>
    /// <remarks>
    /// Direction is meaning, not presentation: "this VM runs on that host" and
    /// "this host runs that VM" are different sentences and the client must not
    /// have to guess which one it is holding.
    /// </remarks>
    public required bool IsOutgoing { get; init; }

    public required string OtherId { get; init; }

    public required string OtherName { get; init; }

    public required EntityKind OtherKind { get; init; }

    public required HealthState OtherHealth { get; init; }
}

/// <summary>An entity with everything its own page needs (tier 2 of ADR-0007).</summary>
public sealed record EntityDetailView
{
    public required EntityView Entity { get; init; }

    /// <summary>Evidence about which real-world thing this is.</summary>
    public required IReadOnlyList<IdentityMarkView> Marks { get; init; }

    public required IReadOnlyList<RelationshipView> Relationships { get; init; }

    /// <summary>
    /// The same instances the inbox shows, filtered to this entity.
    /// </summary>
    /// <remarks>
    /// Filtered, never recomputed. See ADR-0007 §1.
    /// </remarks>
    public required IReadOnlyList<AlertView> Alerts { get; init; }
}

public sealed record IdentityMarkView
{
    public required IdentityMarkKind Kind { get; init; }

    public required string Value { get; init; }

    public required string Source { get; init; }
}

/// <summary>How a collector is behaving.</summary>
public sealed record CollectorView
{
    public required string InstanceId { get; init; }

    /// <summary>Inventory or metrics. The two fail independently — see ADR-0009.</summary>
    public required string Role { get; init; }

    public required HealthState Health { get; init; }

    public required int ConsecutiveFailures { get; init; }

    public required bool IsBackingOff { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    public string? LastFailureDetail { get; init; }
}

/// <summary>The triage summary: what is happening right now.</summary>
public sealed record OverviewView
{
    /// <summary>When this answer was computed, so the client can show its age.</summary>
    /// <remarks>
    /// Required by the wallboard rule in ADR-0007 §6: stale data must never be
    /// presented as fresh. A client that cannot reach the server keeps the last
    /// answer and says how old it is, rather than showing it as current.
    /// </remarks>
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required int CriticalAlerts { get; init; }

    public required int WarningAlerts { get; init; }

    /// <summary>Visible alerts nobody has taken yet.</summary>
    public required int UnacknowledgedAlerts { get; init; }

    public required int SuppressedAlerts { get; init; }

    public required IReadOnlyDictionary<string, int> EntitiesByHealth { get; init; }

    public required int VanishedEntities { get; init; }

    /// <summary>
    /// Collectors that are currently failing.
    /// </summary>
    /// <remarks>
    /// On the overview rather than buried in a settings page, because it
    /// changes what every other number on this screen means: anything a failing
    /// collector covers is unknown, not healthy.
    /// </remarks>
    public required int FailingCollectors { get; init; }

    /// <summary>
    /// The oldest successful read across all collectors, or null if none has
    /// ever succeeded.
    /// </summary>
    /// <remarks>
    /// The honest answer to "how current is this screen": as current as the
    /// stalest thing on it.
    /// </remarks>
    public DateTimeOffset? OldestSuccessfulReadUtc { get; init; }
}
