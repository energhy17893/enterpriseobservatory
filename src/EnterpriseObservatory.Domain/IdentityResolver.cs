namespace EnterpriseObservatory.Domain;

/// <summary>
/// Decides which observed records refer to the same real-world thing.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place identity is decided. Collectors report what marks
/// they saw; they never conclude that two records are the same box. In the
/// previous product that conclusion was drawn in at least four places
/// (<c>SharesIdentity</c>, <c>MatchesIloInventory</c>, <c>MatchesIloNode</c>,
/// <c>OmeAlerting</c>) which drifted apart over time.
/// </para>
/// <para>
/// Because <see cref="RelationshipKind.SameAs"/> is symmetric and transitive it
/// is an equivalence relation, so resolution reduces to finding connected
/// components — implemented here with union-find. See ADR-0004.
/// </para>
/// </remarks>
public static class IdentityResolver
{
    /// <summary>
    /// Marks unique enough to imply identity on their own.
    /// </summary>
    /// <remarks>
    /// An IP address is deliberately absent. Addresses are reused, a BMC has a
    /// different address from the host it manages, and NAT makes them
    /// ambiguous. The previous product treated an IP intersection as sufficient,
    /// which can fold two unrelated machines into one.
    /// </remarks>
    private static bool IsStrong(IdentityMarkKind kind) => kind
        is IdentityMarkKind.HardwareUuid
        or IdentityMarkKind.SerialNumber
        or IdentityMarkKind.ServiceTag
        or IdentityMarkKind.WorldWideName;

    /// <summary>
    /// Decides whether two sets of marks describe the same thing.
    /// </summary>
    /// <returns>The marks that justify the match; empty when they do not match.</returns>
    /// <remarks>
    /// One strong mark is enough. Weak marks need corroboration: two of them.
    /// A shared short hostname alone, for example, is a common false positive
    /// across a cluster whose members follow a naming convention.
    /// </remarks>
    public static IReadOnlyList<IdentityMark> Match(
        IReadOnlyList<IdentityMark> left,
        IReadOnlyList<IdentityMark> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var shared = new List<IdentityMark>();
        foreach (var l in left)
        {
            foreach (var r in right)
            {
                if (l.Kind == r.Kind && string.Equals(l.Value, r.Value, StringComparison.Ordinal))
                {
                    shared.Add(l);
                    break;
                }
            }
        }

        if (shared.Count == 0)
        {
            return [];
        }

        var hasStrong = shared.Exists(m => IsStrong(m.Kind));
        return hasStrong || shared.Count >= 2 ? shared : [];
    }

    /// <summary>
    /// Groups entities into equivalence classes and produces the
    /// <see cref="RelationshipKind.SameAs"/> edges that justify each grouping.
    /// </summary>
    public static IdentityResolution Resolve(
        IReadOnlyList<Entity> entities,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var parent = new int[entities.Count];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // path halving
                x = parent[x];
            }

            return x;
        }

        var edges = new List<Relationship>();

        for (var i = 0; i < entities.Count; i++)
        {
            for (var j = i + 1; j < entities.Count; j++)
            {
                var evidence = Match(entities[i].Marks, entities[j].Marks);
                if (evidence.Count == 0)
                {
                    continue;
                }

                edges.Add(new Relationship
                {
                    From = entities[i].Id,
                    To = entities[j].Id,
                    Kind = RelationshipKind.SameAs,
                    Evidence = evidence,
                    ObservedAtUtc = observedAtUtc,
                });

                var (ri, rj) = (Find(i), Find(j));
                if (ri != rj)
                {
                    parent[ri] = rj;
                }
            }
        }

        var groups = new Dictionary<int, List<EntityId>>();
        for (var i = 0; i < entities.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var members))
            {
                members = [];
                groups[root] = members;
            }

            members.Add(entities[i].Id);
        }

        return new IdentityResolution
        {
            Components = [.. groups.Values.Select(IReadOnlyList<EntityId> (m) => m)],
            SameAsEdges = edges,
        };
    }
}

/// <summary>The outcome of resolving identity across observed entities.</summary>
public sealed record IdentityResolution
{
    /// <summary>
    /// Equivalence classes. Every entity appears in exactly one component;
    /// an unmatched entity forms a component of one.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<EntityId>> Components { get; init; }

    /// <summary>The edges that justify the grouping, each carrying its evidence.</summary>
    public required IReadOnlyList<Relationship> SameAsEdges { get; init; }
}
