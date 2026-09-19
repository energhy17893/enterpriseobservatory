namespace EnterpriseObservatory.Domain;

/// <summary>
/// The closed vocabulary of edges between entities.
/// </summary>
/// <remarks>
/// <para>
/// Collectors may not invent relationship kinds. Adding one requires an ADR.
/// A closed vocabulary is what keeps the graph queryable: an impact-analysis
/// algorithm cannot reason about an edge type it has never heard of.
/// </para>
/// <para>See ADR-0004.</para>
/// </remarks>
public enum RelationshipKind
{
    /// <summary>Containment. Child to parent. Acyclic. Health rolls up.</summary>
    PartOf = 0,

    /// <summary>Execution. Guest to host.</summary>
    RunsOn = 1,

    /// <summary>
    /// Identity equivalence — these two records are the same real thing.
    /// Symmetric and transitive.
    /// </summary>
    SameAs = 2,

    /// <summary>
    /// Physical connection, e.g. an HBA port to a switch port. Symmetric, and
    /// may legitimately form cycles: a redundant fabric is a cyclic graph.
    /// </summary>
    ConnectedTo = 3,

    /// <summary>Logical provisioning. Consumer to provider.</summary>
    BackedBy = 4,

    /// <summary>Management plane. Managed thing to its manager.</summary>
    ManagedBy = 5,
}

/// <summary>Behavioural class of a <see cref="RelationshipKind"/>.</summary>
public enum EdgeClass
{
    /// <summary>Acyclic, health propagates child to parent.</summary>
    Containment,

    /// <summary>Acyclic, health propagates provider to consumer.</summary>
    Dependency,

    /// <summary>An equivalence relation. No health propagation — it is the same thing.</summary>
    Identity,

    /// <summary>May contain cycles. Excluded from health propagation.</summary>
    Physical,
}

/// <summary>A typed, directed edge between two entities.</summary>
public sealed record Relationship
{
    public required EntityId From { get; init; }

    public required EntityId To { get; init; }

    public required RelationshipKind Kind { get; init; }

    /// <summary>
    /// Why we believe this edge exists.
    /// </summary>
    /// <remarks>
    /// Carried so that an identity match can be audited and, if wrong,
    /// explained and withdrawn. Modelling a match as a retractable claim
    /// rather than a merge is the point of ADR-0004.
    /// </remarks>
    public IReadOnlyList<IdentityMark> Evidence { get; init; } = [];

    public required DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>Facts about relationship kinds that algorithms depend on.</summary>
public static class RelationshipRules
{
    /// <summary>The behavioural class of a kind.</summary>
    public static EdgeClass ClassOf(RelationshipKind kind) => kind switch
    {
        RelationshipKind.PartOf => EdgeClass.Containment,
        RelationshipKind.RunsOn => EdgeClass.Dependency,
        RelationshipKind.BackedBy => EdgeClass.Dependency,
        RelationshipKind.ManagedBy => EdgeClass.Dependency,
        RelationshipKind.SameAs => EdgeClass.Identity,
        RelationshipKind.ConnectedTo => EdgeClass.Physical,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled relationship kind."),
    };

    /// <summary>
    /// Whether an edge may be traversed both ways.
    /// </summary>
    public static bool IsSymmetric(RelationshipKind kind) =>
        kind is RelationshipKind.SameAs or RelationshipKind.ConnectedTo;

    /// <summary>
    /// Whether cycles are permitted.
    /// </summary>
    /// <remarks>
    /// Only physical connectivity may cycle. A redundant SAN fabric is supposed
    /// to contain loops; forbidding them would make the product unable to model
    /// the very thing it exists to diagnose. Health propagation avoids the
    /// problem by not traversing these edges at all, rather than by banning them.
    /// </remarks>
    public static bool MayContainCycles(RelationshipKind kind) =>
        ClassOf(kind) == EdgeClass.Physical;

    /// <summary>
    /// Whether health propagates along this edge.
    /// </summary>
    public static bool PropagatesHealth(RelationshipKind kind) =>
        ClassOf(kind) is EdgeClass.Containment or EdgeClass.Dependency;

    /// <summary>Which way a failure travels along an edge.</summary>
    public static ImpactDirection ImpactFlow(RelationshipKind kind) => kind switch
    {
        // A failed part degrades the whole it belongs to: the edge already
        // points child to parent, so impact runs with it.
        RelationshipKind.PartOf => ImpactDirection.WithEdge,

        // The edge reads "guest runs on host"; the failure runs the other way.
        // A host going down takes its guests with it, never the reverse.
        RelationshipKind.RunsOn => ImpactDirection.AgainstEdge,

        // The edge reads "consumer is backed by provider"; again the failure
        // runs from provider to consumer.
        RelationshipKind.BackedBy => ImpactDirection.AgainstEdge,

        // Physical connectivity carries a fault both ways: a dead SFP is as
        // visible from the HBA as from the switch port.
        RelationshipKind.ConnectedTo => ImpactDirection.BothWays,

        // The same machine seen twice. Anything wrong with one is wrong with
        // the other by definition.
        RelationshipKind.SameAs => ImpactDirection.BothWays,

        // Deliberately none. A manager failing is a loss of visibility, not a
        // shared fault — and everything in an estate is managed by the same
        // one or two things, so correlating through it would collapse every
        // alert in the product into a single "event".
        RelationshipKind.ManagedBy => ImpactDirection.Neither,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled relationship kind."),
    };
}

/// <summary>
/// Which way a failure travels along a relationship.
/// </summary>
/// <remarks>
/// <para>
/// A different question from <see cref="RelationshipRules.PropagatesHealth"/>,
/// and the two must not be conflated. Health rolls <em>up</em>: a failed disk
/// makes its host unhealthy. Impact rolls <em>down</em>: a failed host makes
/// its guests suffer. Using one rule for both would group every sibling in a
/// cluster as a single incident.
/// </para>
/// <para>
/// This is what lets the product say "these twelve alerts are one event"
/// and show the path that proves it. See ADR-0007 §5.2.
/// </para>
/// </remarks>
public enum ImpactDirection
{
    /// <summary>A failure does not travel along this edge at all.</summary>
    Neither = 0,

    /// <summary>From the edge's <c>From</c> to its <c>To</c>.</summary>
    WithEdge,

    /// <summary>From the edge's <c>To</c> to its <c>From</c>.</summary>
    AgainstEdge,

    /// <summary>Either way.</summary>
    BothWays,
}
