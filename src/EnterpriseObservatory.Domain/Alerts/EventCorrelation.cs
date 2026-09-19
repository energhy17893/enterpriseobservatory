namespace EnterpriseObservatory.Domain.Alerts;

/// <summary>One step of the path that proves a group.</summary>
/// <param name="From">The entity a failure travelled from.</param>
/// <param name="Kind">The relationship it travelled along.</param>
/// <param name="To">The entity it reached.</param>
/// <remarks>
/// Carried so the interface can answer "why was this grouped". ADR-0007 names
/// that as a requirement rather than a nicety: topological correlation is only
/// as good as the graph, so a wrong group has to be visibly wrong rather than
/// merely wrong.
/// </remarks>
public readonly record struct CorrelationLink(EntityId From, RelationshipKind Kind, EntityId To);

/// <summary>
/// Several alerts that are one thing going wrong.
/// </summary>
/// <remarks>
/// Only ever produced where the graph proves a path. The other kind of
/// correlation — things that merely happened together — is a
/// <see cref="TemporalSuggestion"/>, and is a different type precisely so that
/// no caller can fold one by accident. See ADR-0007 §5.2.
/// </remarks>
public sealed record CorrelatedEvent
{
    /// <summary>Stable while the event lasts, so the interface can keep it open.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>The worst severity in the group; what the header shows.</summary>
    public required AlertSeverity Severity { get; init; }

    /// <summary>The entity the failure appears to start from.</summary>
    public required EntityId Root { get; init; }

    /// <summary>
    /// Every alert in the group, worst first.
    /// </summary>
    /// <remarks>
    /// The real count, never a sample. An operator will not accept a folded
    /// group without seeing what was folded into it — ADR-0007 §5.1.
    /// </remarks>
    public required IReadOnlyList<AlertFingerprint> Alerts { get; init; }

    public required IReadOnlyList<EntityId> Entities { get; init; }

    public required DateTimeOffset FirstSeenUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>The path from the root to each other entity.</summary>
    public required IReadOnlyList<CorrelationLink> Explanation { get; init; }
}

/// <summary>
/// Alerts that started together and may be one thing.
/// </summary>
/// <remarks>
/// <para>
/// A suggestion, never a group. The evidence is only that several alerts
/// appeared within a short window, which is exactly as true of one failure as
/// of two unrelated ones happening at lunchtime.
/// </para>
/// <para>
/// A separate type rather than a flag on <see cref="CorrelatedEvent"/>, so a
/// caller cannot fold one by forgetting to check. The product presents only
/// what it can prove; what it cannot prove it points at.
/// </para>
/// </remarks>
public sealed record TemporalSuggestion
{
    public required IReadOnlyList<AlertFingerprint> Alerts { get; init; }

    public required DateTimeOffset WithinUtc { get; init; }

    public required TimeSpan Window { get; init; }
}

/// <summary>What correlation made of one set of alerts.</summary>
public sealed record CorrelationResult
{
    public IReadOnlyList<CorrelatedEvent> Events { get; init; } = [];

    /// <summary>Offered to the operator, never folded.</summary>
    public IReadOnlyList<TemporalSuggestion> Suggestions { get; init; } = [];

    /// <summary>Alerts that belong to no event.</summary>
    public IReadOnlyList<AlertFingerprint> Ungrouped { get; init; } = [];
}

/// <summary>How hard correlation is allowed to reach.</summary>
public sealed record CorrelationPolicy
{
    /// <summary>
    /// How many relationships a failure may travel before the claim stops
    /// being credible.
    /// </summary>
    /// <remarks>
    /// Four covers the chain the product exists to make visible — switch port
    /// to HBA to host to virtual machine — and stops well short of the range
    /// at which everything in an estate is connected to everything else.
    /// </remarks>
    public int MaxHops { get; init; } = 4;

    /// <summary>How close together alerts must start to be worth pointing at.</summary>
    public TimeSpan TemporalWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many alerts must coincide before coincidence is worth mentioning.
    /// </summary>
    /// <remarks>
    /// Two alerts a minute apart is a Tuesday. Enough of them at once is worth
    /// an operator's glance even with nothing to prove it.
    /// </remarks>
    public int MinimumTemporalAlerts { get; init; } = 3;

    public static CorrelationPolicy Default { get; } = new();
}

/// <summary>
/// Works out which alerts are one thing going wrong.
/// </summary>
/// <remarks>
/// <para>
/// This is the concrete return on having a topology model at all. The previous
/// product could not make this distinction because it had no graph: twelve
/// alerts from one dead SFP were twelve problems, and the operator did the
/// correlating in their head at three in the morning.
/// </para>
/// <para>
/// The rule is directed reachability over impact, not connectivity. Two hosts
/// in one cluster are connected — through the cluster — and are emphatically
/// not one event. A failure travels from a host to the virtual machines on it
/// and not to its siblings, and following that direction is what keeps the
/// claim honest. See <see cref="ImpactDirection"/>.
/// </para>
/// <para>
/// Pure: no clock beyond what it is handed, no storage, no I/O.
/// </para>
/// </remarks>
public static class EventCorrelator
{
    public static CorrelationResult Correlate(
        IReadOnlyList<AlertInstance> alerts,
        EntityGraph graph,
        CorrelationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(policy);

        // Only alerts about something can be correlated by topology. A
        // collector being unreachable is about the collector, not the estate,
        // and has no entity to reason from.
        var byEntity = alerts
            .Where(a => a.Entity is not null)
            .GroupBy(a => a.Entity!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var adjacency = BuildImpactGraph(graph);
        var reach = Reachability(byEntity.Keys, adjacency, policy.MaxHops);

        var roots = FindRoots(byEntity.Keys, reach);

        var owner = AssignToNearestRoot(roots, byEntity.Keys, reach);
        var events = new List<CorrelatedEvent>();
        var grouped = new HashSet<AlertFingerprint>();

        foreach (var root in roots)
        {
            var members = owner
                .Where(pair => pair.Value == root)
                .Select(pair => pair.Key)
                .OrderBy(e => e.Value, StringComparer.Ordinal)
                .ToList();

            // One entity's own alerts are not an event; they are that entity's
            // alerts. Folding them would add a layer that explains nothing.
            if (members.Count < 2)
            {
                continue;
            }

            var memberAlerts = members
                .SelectMany(e => byEntity[e])
                .OrderByDescending(a => a.Severity)
                .ThenBy(a => a.FirstSeenUtc)
                .ToList();

            events.Add(new CorrelatedEvent
            {
                Id = $"event:{root.Value}",
                Title = TitleFor(byEntity[root], members.Count),
                Severity = memberAlerts.Max(a => a.Severity),
                Root = root,
                Alerts = [.. memberAlerts.Select(a => a.Fingerprint)],
                Entities = members,
                FirstSeenUtc = memberAlerts.Min(a => a.FirstSeenUtc),
                LastSeenUtc = memberAlerts.Max(a => a.LastSeenUtc),
                Explanation = Explain(root, members, adjacency, policy.MaxHops),
            });

            foreach (var alert in memberAlerts)
            {
                grouped.Add(alert.Fingerprint);
            }
        }

        var ungrouped = alerts.Where(a => !grouped.Contains(a.Fingerprint)).ToList();

        return new CorrelationResult
        {
            Events = [.. events.OrderByDescending(e => e.Severity).ThenBy(e => e.FirstSeenUtc)],
            Suggestions = Suggest(ungrouped, policy),
            Ungrouped = [.. ungrouped.Select(a => a.Fingerprint)],
        };
    }

    // --- the impact graph --------------------------------------------------

    /// <summary>
    /// Turns the topology into the directed graph a failure travels along.
    /// </summary>
    /// <remarks>
    /// Built once per correlation rather than walked per pair: the estate has
    /// thousands of edges and a handful of alerting entities, so one pass over
    /// the edges beats repeated searches through them.
    /// </remarks>
    private static Dictionary<EntityId, List<(EntityId To, RelationshipKind Kind)>> BuildImpactGraph(
        EntityGraph graph)
    {
        var adjacency = new Dictionary<EntityId, List<(EntityId, RelationshipKind)>>();

        void Add(EntityId from, EntityId to, RelationshipKind kind)
        {
            if (!adjacency.TryGetValue(from, out var edges))
            {
                edges = [];
                adjacency[from] = edges;
            }

            edges.Add((to, kind));
        }

        foreach (var edge in graph.Relationships)
        {
            switch (RelationshipRules.ImpactFlow(edge.Kind))
            {
                case ImpactDirection.WithEdge:
                    Add(edge.From, edge.To, edge.Kind);
                    break;

                case ImpactDirection.AgainstEdge:
                    Add(edge.To, edge.From, edge.Kind);
                    break;

                case ImpactDirection.BothWays:
                    Add(edge.From, edge.To, edge.Kind);
                    Add(edge.To, edge.From, edge.Kind);
                    break;
            }
        }

        return adjacency;
    }

    /// <summary>
    /// From each alerting entity, which other alerting entities it reaches and
    /// in how few hops.
    /// </summary>
    private static Dictionary<EntityId, Dictionary<EntityId, int>> Reachability(
        IEnumerable<EntityId> sources,
        Dictionary<EntityId, List<(EntityId To, RelationshipKind Kind)>> adjacency,
        int maxHops)
    {
        var alerting = sources.ToHashSet();
        var reach = new Dictionary<EntityId, Dictionary<EntityId, int>>();

        foreach (var source in alerting)
        {
            var found = new Dictionary<EntityId, int>();
            var seen = new HashSet<EntityId> { source };
            var frontier = new Queue<(EntityId Entity, int Hops)>();

            frontier.Enqueue((source, 0));

            while (frontier.Count > 0)
            {
                var (entity, hops) = frontier.Dequeue();

                if (hops == maxHops || !adjacency.TryGetValue(entity, out var edges))
                {
                    continue;
                }

                foreach (var (next, _) in edges)
                {
                    if (!seen.Add(next))
                    {
                        continue;
                    }

                    // Intermediates need not be alerting: the switch port, the
                    // HBA and the host may each be silent while the fault shows
                    // up at either end of the chain.
                    if (alerting.Contains(next))
                    {
                        found[next] = hops + 1;
                    }

                    frontier.Enqueue((next, hops + 1));
                }
            }

            reach[source] = found;
        }

        return reach;
    }

    /// <summary>
    /// Where the product points first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A root is an alerting entity with nothing strictly upstream of it —
    /// nothing that reaches it without it reaching back. Anything upstream of a
    /// failure is a better candidate for its cause.
    /// </para>
    /// <para>
    /// "Strictly" is load-bearing. Physical connectivity is symmetric, so a
    /// dead SFP and the HBA it feeds each reach the other; a rule that asked
    /// merely whether anything reaches an entity would find no root at all and
    /// group nothing — which is the chain this whole feature exists for. Where
    /// several entities reach each other, one of them is chosen so that the
    /// event has a single place to point.
    /// </para>
    /// </remarks>
    private static List<EntityId> FindRoots(
        IEnumerable<EntityId> alerting, Dictionary<EntityId, Dictionary<EntityId, int>> reach)
    {
        bool Reaches(EntityId from, EntityId to) =>
            reach.TryGetValue(from, out var found) && found.ContainsKey(to);

        var ordered = alerting.OrderBy(e => e.Value, StringComparer.Ordinal).ToList();

        var candidates = ordered
            .Where(entity => !ordered.Any(other =>
                other != entity && Reaches(other, entity) && !Reaches(entity, other)))
            .ToList();

        // Collapse each mutually reachable set to one representative, so an
        // event has one root rather than several that are all equally its
        // beginning.
        var roots = new List<EntityId>();

        foreach (var candidate in candidates)
        {
            if (!roots.Any(chosen => Reaches(chosen, candidate) && Reaches(candidate, chosen)))
            {
                roots.Add(candidate);
            }
        }

        return roots;
    }

    /// <summary>
    /// Gives every alerting entity to exactly one root.
    /// </summary>
    /// <remarks>
    /// Exactly one, because an alert appearing in two events would be counted
    /// twice and acted on twice. Where two roots both reach something, the
    /// nearer one wins: a failure two hops away explains it better than one
    /// four hops away.
    /// </remarks>
    private static Dictionary<EntityId, EntityId> AssignToNearestRoot(
        IReadOnlyList<EntityId> roots,
        IEnumerable<EntityId> alerting,
        Dictionary<EntityId, Dictionary<EntityId, int>> reach)
    {
        var owner = new Dictionary<EntityId, EntityId>();

        foreach (var entity in alerting)
        {
            EntityId? best = null;
            var bestHops = int.MaxValue;

            foreach (var root in roots)
            {
                if (root == entity)
                {
                    best = root;
                    bestHops = 0;
                    break;
                }

                if (reach[root].TryGetValue(entity, out var hops) && hops < bestHops)
                {
                    best = root;
                    bestHops = hops;
                }
            }

            if (best is { } chosen)
            {
                owner[entity] = chosen;
            }
        }

        return owner;
    }

    /// <summary>
    /// The path from the root to each member, for the interface to show.
    /// </summary>
    /// <remarks>
    /// Recomputed rather than remembered during the search, because the search
    /// is breadth-first over the whole neighbourhood and carrying a path for
    /// every node visited would cost more than walking it again for the few
    /// that turned out to matter.
    /// </remarks>
    private static List<CorrelationLink> Explain(
        EntityId root,
        IReadOnlyList<EntityId> members,
        Dictionary<EntityId, List<(EntityId To, RelationshipKind Kind)>> adjacency,
        int maxHops)
    {
        var links = new List<CorrelationLink>();
        var seen = new HashSet<CorrelationLink>();
        var previous = new Dictionary<EntityId, CorrelationLink>();
        var visited = new HashSet<EntityId> { root };
        var frontier = new Queue<(EntityId Entity, int Hops)>();

        frontier.Enqueue((root, 0));

        while (frontier.Count > 0)
        {
            var (entity, hops) = frontier.Dequeue();

            if (hops == maxHops || !adjacency.TryGetValue(entity, out var edges))
            {
                continue;
            }

            foreach (var (next, kind) in edges)
            {
                if (!visited.Add(next))
                {
                    continue;
                }

                previous[next] = new CorrelationLink(entity, kind, next);
                frontier.Enqueue((next, hops + 1));
            }
        }

        foreach (var member in members.Where(m => m != root))
        {
            // Walked backwards from the member and then reversed, so the
            // reader gets the chain the way a failure travelled it: the switch
            // port first, the virtual machine last. Listed the other way it
            // reads as a puzzle rather than an explanation.
            var chain = new List<CorrelationLink>();
            var step = member;

            while (previous.TryGetValue(step, out var link))
            {
                chain.Add(link);
                step = link.From;
            }

            chain.Reverse();

            foreach (var link in chain.Where(link => seen.Add(link)))
            {
                links.Add(link);
            }
        }

        return links;
    }

    // --- coincidence, offered but never folded -----------------------------

    private static List<TemporalSuggestion> Suggest(
        IReadOnlyList<AlertInstance> ungrouped, CorrelationPolicy policy)
    {
        var suggestions = new List<TemporalSuggestion>();
        var ordered = ungrouped.OrderBy(a => a.FirstSeenUtc).ToList();
        var index = 0;

        while (index < ordered.Count)
        {
            var start = ordered[index].FirstSeenUtc;
            var end = index;

            while (end + 1 < ordered.Count && ordered[end + 1].FirstSeenUtc - start <= policy.TemporalWindow)
            {
                end++;
            }

            var count = end - index + 1;

            if (count >= policy.MinimumTemporalAlerts)
            {
                suggestions.Add(new TemporalSuggestion
                {
                    Alerts = [.. ordered.Skip(index).Take(count).Select(a => a.Fingerprint)],
                    WithinUtc = start,
                    Window = policy.TemporalWindow,
                });
            }

            index = end + 1;
        }

        return suggestions;
    }

    private static string TitleFor(IReadOnlyList<AlertInstance> rootAlerts, int entityCount)
    {
        var worst = rootAlerts.MaxBy(a => a.Severity)!;

        var things = entityCount == 1 ? "thing" : "things";

        return $"{worst.Title} — affecting {entityCount} {things}";
    }
}
