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

    /// <summary>
    /// How long a silent source's annotations are carried forward before they
    /// read as absent (ADR-0027 §3).
    /// </summary>
    /// <remarks>
    /// ADR-0026's carry-forward limit, the raw retention of two days: past it
    /// the evidence behind the value is gone too, so showing it would be
    /// showing a guess. Absent, not stale — a rule reading the key then
    /// behaves as if the source never said anything.
    /// </remarks>
    public TimeSpan AnnotationCarryForward { get; init; } = TimeSpan.FromDays(2);

    public static EntityRetentionPolicy Default { get; } = new();
}

/// <summary>
/// What a source says about an entity another source owns (ADR-0027).
/// </summary>
/// <remarks>
/// <para>
/// ADR-0027, "a source annotates an entity it does not own; it does not
/// replace it": SimpliVity's view of an ESXi host is the vSphere host, not a
/// second one. The owner keeps the entity — its name, kind, marks and
/// relationships; the annotating source adds settings under its own
/// namespace and nothing else. The references are vROps/Aria (one adapter
/// owns an object, others add properties to it) and Dynatrace (a second
/// source's data merges into the existing entity rather than opening one).
/// </para>
/// <para>
/// Applied only to an entity that already exists. A reference nothing owns
/// is the collector's to report as "could not fold", never an entity to
/// open.
/// </para>
/// </remarks>
public sealed record EntityAnnotation
{
    /// <summary>The entity another source owns.</summary>
    public required EntityId Entity { get; init; }

    /// <summary>The source's namespace, e.g. <c>simplivity</c>.</summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// The settings to add. Every key is <see cref="Namespace"/> followed by
    /// a dot, which is what makes a collision with the owner's own settings,
    /// or another source's, structurally impossible.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Settings { get; init; }

    /// <summary>Who said so. Stamped by the pipeline, like an entity's.</summary>
    public string SourceInstanceId { get; init; } = string.Empty;

    /// <summary>When it was read; the carry-forward limit runs from here.</summary>
    public DateTimeOffset ReadAtUtc { get; init; }
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

    /// <summary>
    /// The annotations currently applied to <see cref="Entities"/>, fresh or
    /// carried forward (ADR-0027).
    /// </summary>
    /// <remarks>
    /// Kept so the next merge can carry a silent source's namespace forward
    /// after the owner has replaced the entity. In memory only: the store
    /// does not persist entity settings at all, so after a restart this is
    /// empty and every annotation reads as absent until its source answers
    /// again — the safe direction, and the same as the owner's own settings,
    /// which are also rebuilt by the first inventory cycle.
    /// </remarks>
    public IReadOnlyList<EntityAnnotation> Annotations { get; init; } = [];

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
    /// <para>
    /// <paramref name="annotations"/> are overlaid last and change nothing but
    /// <see cref="Entity.Settings"/> under their own namespace. See
    /// <see cref="Annotate"/>.
    /// </para>
    /// </remarks>
    public EntityGraph Merge(
        IReadOnlyList<Entity> observed,
        IReadOnlyList<Relationship> relationships,
        IReadOnlyCollection<string> reportingSources,
        DateTimeOffset nowUtc,
        EntityRetentionPolicy policy,
        IReadOnlyList<EntityAnnotation>? annotations = null)
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

        var applied = Annotate(next, annotations ?? [], reporting, nowUtc, policy);

        return this with
        {
            Entities = next,
            Relationships = MergeRelationships(relationships, reporting, next),
            Annotations = applied,
        };
    }

    /// <summary>
    /// Overlays annotations onto the entities just merged (ADR-0027).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fresh annotations replace, per entity and namespace, whatever was
    /// carried; a silent source's annotations are carried forward until
    /// <see cref="EntityRetentionPolicy.AnnotationCarryForward"/> (ADR-0026's
    /// two days) and then read as absent, never forever. The carried set
    /// lives in memory (see <see cref="Annotations"/>): a restart resets it
    /// to absent, the safe direction.
    /// </para>
    /// <para>
    /// Every namespace any annotation has used is first stripped from every
    /// entity, so an entity kept from a silent owner — which still holds last
    /// cycle's overlay — cannot keep an expired value by accident.
    /// </para>
    /// <para>
    /// Throws when two sources write the same namespace on the same entity in
    /// one cycle, or a key is outside its namespace: either is a defect, and
    /// absorbing it would hide one source's word behind the other's. A fresh
    /// annotation does replace a <em>carried</em> one from a different source,
    /// because the carried one is older evidence — a connection renamed
    /// within the carry-forward window must not stop the inventory cycle.
    /// </para>
    /// </remarks>
    private List<EntityAnnotation> Annotate(
        Dictionary<EntityId, Entity> entities,
        IReadOnlyList<EntityAnnotation> fresh,
        HashSet<string> reporting,
        DateTimeOffset nowUtc,
        EntityRetentionPolicy policy)
    {
        var claimed = new Dictionary<(EntityId, string), string>();

        foreach (var annotation in fresh)
        {
            var prefix = annotation.Namespace + ".";

            if (annotation.Settings.Keys.FirstOrDefault(k => !k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                is { } stray)
            {
                throw new ArgumentException(
                    $"'{annotation.SourceInstanceId}' annotated '{annotation.Entity}' with '{stray}', " +
                    $"which is outside its namespace '{annotation.Namespace}' (ADR-0027).",
                    nameof(fresh));
            }

            if (!claimed.TryAdd((annotation.Entity, annotation.Namespace), annotation.SourceInstanceId))
            {
                throw new InvalidOperationException(
                    $"'{annotation.Entity}' was annotated twice under '{annotation.Namespace}' in one cycle, " +
                    $"by '{claimed[(annotation.Entity, annotation.Namespace)]}' and '{annotation.SourceInstanceId}'. " +
                    "One namespace has one writer per entity (ADR-0027).");
            }
        }

        var carried = Annotations.Where(a =>
            !reporting.Contains(a.SourceInstanceId) &&
            nowUtc - a.ReadAtUtc <= policy.AnnotationCarryForward &&
            !claimed.ContainsKey((a.Entity, a.Namespace)));

        // Never creates an entity: an annotation on something nobody owns is
        // dropped here and was the collector's to report as "could not fold".
        var current = fresh.Concat(carried).Where(a => entities.ContainsKey(a.Entity)).ToList();

        var namespaces = Annotations.Concat(current)
            .Select(a => a.Namespace + ".")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (namespaces.Count == 0)
        {
            return current;
        }

        var byEntity = current.ToLookup(a => a.Entity);

        foreach (var (id, entity) in entities.ToList())
        {
            var mine = byEntity[id].ToList();
            var stale = entity.Settings.Keys.Any(k => namespaces.Any(n => k.StartsWith(n, StringComparison.OrdinalIgnoreCase)));

            if (mine.Count == 0 && !stale)
            {
                continue;
            }

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (key, value) in entity.Settings)
            {
                if (!namespaces.Any(n => key.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                {
                    settings[key] = value;
                }
            }

            foreach (var (key, value) in mine.SelectMany(a => a.Settings))
            {
                settings[key] = value;
            }

            entities[id] = entity with { Settings = settings };
        }

        return current;
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
