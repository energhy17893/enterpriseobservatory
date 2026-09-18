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
}
