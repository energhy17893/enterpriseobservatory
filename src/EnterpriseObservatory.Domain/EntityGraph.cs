namespace EnterpriseObservatory.Domain;

/// <summary>How long a vanished entity is kept before it is forgotten.</summary>
public sealed record EntityRetentionPolicy
{
    /// <summary>
    /// How long an entity is retained after it stops being reported.
    /// </summary>
    /// <remarks>
    /// Long enough to cover a planned maintenance window or a hardware swap,
    /// short enough that the graph does not fill with things that left a year
    /// ago. See ADR-0004.
    /// </remarks>
    public TimeSpan VanishedRetention { get; init; } = TimeSpan.FromDays(30);

    public static EntityRetentionPolicy Default { get; } = new();
}

/// <summary>
/// Everything currently known, and how it is connected.
/// </summary>
/// <remarks>
/// <para>
/// Immutable: a cycle produces a new graph rather than mutating the old one, so
/// a reader is never looking at a half-applied update.
/// </para>
/// <para>
/// See ADR-0003 and ADR-0004.
/// </para>
/// </remarks>
public sealed record EntityGraph
{
    public static EntityGraph Empty { get; } = new();

    public IReadOnlyDictionary<EntityId, Entity> Entities { get; init; } =
        new Dictionary<EntityId, Entity>();

    public IReadOnlyList<Relationship> Relationships { get; init; } = [];

    /// <summary>Entities a collector is currently reporting.</summary>
    public IEnumerable<Entity> Active =>
        Entities.Values.Where(e => e.ObservationState != ObservationState.Vanished);

    /// <summary>Entities retained after they stopped being reported.</summary>
    public IEnumerable<Entity> Vanished =>
        Entities.Values.Where(e => e.ObservationState == ObservationState.Vanished);

    /// <summary>
    /// Folds one cycle's observations into the graph.
    /// </summary>
    /// <param name="observed">Entities seen this cycle.</param>
    /// <param name="relationships">Edges seen this cycle.</param>
    /// <param name="reportingSources">
    /// The collector instances that actually answered. This is the load-bearing
    /// argument: only a source that reported may cause its own entities to be
    /// treated as vanished.
    /// </param>
    /// <param name="nowUtc">Cycle timestamp.</param>
    /// <param name="policy">Retention.</param>
    /// <remarks>
    /// <para>
    /// A collector that could not be reached leaves its entities exactly as
    /// they were. Marking them vanished would say "these machines are gone"
    /// when the truth is "we could not look" — the two lead to opposite
    /// actions, and the second is already reported as a collector alert.
    /// </para>
    /// <para>
    /// Relationships are replaced wholesale for the reporting sources rather
    /// than merged, because an edge that has disappeared is meaningful: a VM
    /// that moved host must not keep its old <c>RunsOn</c>.
    /// </para>
    /// </remarks>
    public EntityGraph Merge(
        IReadOnlyList<Entity> observed,
        IReadOnlyList<Relationship> relationships,
        IReadOnlyCollection<string> reportingSources,
        DateTimeOffset nowUtc,
        EntityRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(relationships);
        ArgumentNullException.ThrowIfNull(reportingSources);
        ArgumentNullException.ThrowIfNull(policy);

        var reporting = new HashSet<string>(reportingSources, StringComparer.Ordinal);
        var seen = observed.Select(e => e.Id).ToHashSet();
        var next = new Dictionary<EntityId, Entity>(Entities.Count + observed.Count);

        foreach (var (id, existing) in Entities)
        {
            if (seen.Contains(id))
            {
                continue; // superseded below
            }

            // Not reported, but nobody who could have reported it was heard
            // from. We did not look; we cannot conclude it is gone.
            if (!reporting.Contains(existing.SourceInstanceId))
            {
                next[id] = existing;
                continue;
            }

            var vanished = existing.ObservationState == ObservationState.Vanished
                ? existing
                : existing with { ObservationState = ObservationState.Vanished };

            // Retention runs from when it was last seen, not from when it was
            // first noticed missing, so a restart does not reset the clock.
            if (nowUtc - vanished.LastSeenUtc <= policy.VanishedRetention)
            {
                next[id] = vanished;
            }
        }

        foreach (var entity in observed)
        {
            next[entity.Id] = Entities.TryGetValue(entity.Id, out var previous)
                // Preserve what a cycle does not re-report: first-seen time
                // lives on the stored entity, not on the observation.
                ? entity with { ObservationState = ResolveState(entity, previous) }
                : entity;
        }

        return this with
        {
            Entities = next,
            Relationships = MergeRelationships(relationships, reporting, next),
        };
    }

    /// <summary>
    /// Keeps a reporting source's newly observed edges and every edge belonging
    /// to a source that stayed silent.
    /// </summary>
    private List<Relationship> MergeRelationships(
        IReadOnlyList<Relationship> observed,
        HashSet<string> reporting,
        Dictionary<EntityId, Entity> entities) =>
        [.. Relationships
            .Where(r => !ReportedBy(r, reporting, entities))
            .Concat(observed)
            // An edge to an entity that has been forgotten is not an edge.
            .Where(r => entities.ContainsKey(r.From) && entities.ContainsKey(r.To))];

    private static bool ReportedBy(
        Relationship relationship,
        HashSet<string> reporting,
        Dictionary<EntityId, Entity> entities) =>
        entities.TryGetValue(relationship.From, out var from) &&
        reporting.Contains(from.SourceInstanceId);

    /// <summary>
    /// Decides the observation state of something we have just seen again.
    /// </summary>
    /// <remarks>
    /// Maintenance is reported by the collector and wins. Otherwise seeing it
    /// clears a previous vanish: it came back.
    /// </remarks>
    private static ObservationState ResolveState(Entity observed, Entity previous) =>
        observed.ObservationState == ObservationState.InMaintenance
            ? ObservationState.InMaintenance
            : ObservationState.Active;
}
