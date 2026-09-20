using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Host.AllInOne.Collectors;

/// <summary>
/// Tells the metric cycle what to sample, from what the inventory cycle found.
/// </summary>
/// <remarks>
/// <para>
/// This is the join between the two rhythms (ADR-0005). Discovery happens every
/// few minutes; sampling happens every few seconds and asks here what exists
/// rather than re-discovering it. A vCenter that has not yet been inventoried
/// is sampled for nothing at all, which is correct: we would not know what to
/// ask about.
/// </para>
/// <para>
/// Only <see cref="ObservationState.Active"/> entities are sampled. A vanished
/// one is retained so its history survives, but asking vCenter for metrics on
/// an object it no longer has produces one failure per cycle forever.
/// </para>
/// <para>
/// Entities in maintenance <em>are</em> sampled. Maintenance suppresses
/// notification, not observation — a host being patched still produces real
/// numbers, and losing them leaves a hole in the history exactly where someone
/// will later want to look.
/// </para>
/// </remarks>
public sealed class GraphSampleTargetProvider(IEntityGraphStore store, string instanceId)
    : IVsphereSampleTargetProvider
{
    private readonly IEntityGraphStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    private readonly string _instanceId =
        instanceId ?? throw new ArgumentNullException(nameof(instanceId));

    /// <summary>
    /// The prefix the inventory source qualifies its managed object references
    /// with. Managed object references are unique within a vCenter but not
    /// between them, so the entity id carries the source.
    /// </summary>
    private string Prefix => _instanceId + EntityId.Separator;

    public VsphereSampleTargets Current
    {
        get
        {
            var mine = _store.Current.Entities.Values
                .Where(e =>
                    e.ObservationState != ObservationState.Vanished &&
                    string.Equals(e.SourceInstanceId, _instanceId, StringComparison.Ordinal))
                .ToList();

            return new VsphereSampleTargets
            {
                Hosts = MoRefs(mine, EntityKind.EsxiHost),
                VirtualMachines = MoRefs(mine, EntityKind.VirtualMachine),
                Datastores = MoRefs(mine, EntityKind.Datastore),
            };
        }
    }

    public EntityId? ResolveEntity(string moRef)
    {
        var id = EntityId.For(_instanceId, moRef);

        // Checked against the graph rather than returned unconditionally. A
        // sample for an entity we do not know about would attach to nothing,
        // and a measurement with no subject is worse than no measurement: it
        // looks like data.
        return _store.Current.Entities.ContainsKey(id) ? id : null;
    }

    /// <summary>A volume index together with the graph it was built from.</summary>
    /// <remarks>
    /// <para>
    /// One value rather than two fields, and that is the whole point of it.
    /// The index is invalidated by reference — the inventory cycle replaces the
    /// graph wholesale, which is what <see cref="IEntityGraphStore.Replace"/>
    /// means, so a different instance is an exact signal that the index is
    /// stale. That only works while the index and the graph it came from cannot
    /// disagree, and as two separate unsynchronised writes they could.
    /// </para>
    /// <para>
    /// Two reads can be in here at once: the metric cycle is driven from a
    /// source the runner may have abandoned at its timeout without stopping it,
    /// so a read from the previous cycle can still be running. If the older one
    /// published its index after the newer one published its graph, the pair
    /// ended up mismatched — index from the old graph, stamped with the new —
    /// and every later lookup then found them equal and served the old index
    /// for as long as that graph stayed current. A datastore the latest
    /// inventory cycle discovered would resolve to nothing and its latency
    /// samples would be dropped, silently, which is the one failure this
    /// product exists to prevent: missing data that looks like healthy data.
    /// </para>
    /// </remarks>
    private sealed record VolumeIndex(EntityGraph Graph, Dictionary<string, EntityId> Volumes);

    private VolumeIndex? _index;

    /// <remarks>
    /// Held across the rebuild, which is pure dictionary work over a graph
    /// already in memory — never across a vCenter call, where a hang would cost
    /// the next cycle rather than this one.
    /// </remarks>
    private readonly Lock _padlock = new();

    /// <summary>
    /// Finds the datastore a volume identifier belongs to.
    /// </summary>
    /// <remarks>
    /// Indexed rather than scanned. This is called once per datastore
    /// measurement, not once per cycle: on the estate this was written against
    /// that is six counters across forty-one datastores seen from ten hosts,
    /// so roughly two and a half thousand calls every thirty seconds, each of
    /// which was walking two hundred entities and their marks. The comment
    /// that used to sit here said "once per metric cycle" and was simply
    /// wrong about its own caller.
    /// </remarks>
    public EntityId? ResolveVolume(string volumeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(volumeIdentifier))
        {
            return null;
        }

        // Read once. Asking the store again below could hand back a different
        // graph, which would be the same mismatch by another route.
        var graph = _store.Current;

        Dictionary<string, EntityId> volumes;

        lock (_padlock)
        {
            if (_index is not { } index || !ReferenceEquals(graph, index.Graph))
            {
                index = new VolumeIndex(graph, BuildVolumeIndex(graph));
                _index = index;
            }

            volumes = index.Volumes;
        }

        return volumes.TryGetValue(volumeIdentifier, out var datastore) ? datastore : null;
    }

    /// <remarks>
    /// First mark wins where two datastores claim one volume, which should not
    /// happen and is not worth failing a whole cycle over — but it is worth
    /// not silently picking a different one each time the dictionary is
    /// rebuilt, which is what last-wins would do.
    /// </remarks>
    private Dictionary<string, EntityId> BuildVolumeIndex(EntityGraph graph)
    {
        var index = new Dictionary<string, EntityId>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in graph.Entities.Values)
        {
            if (entity.Kind != EntityKind.Datastore ||
                !string.Equals(entity.SourceInstanceId, _instanceId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var mark in entity.Marks)
            {
                if (mark.Kind == IdentityMarkKind.VolumeIdentifier)
                {
                    index.TryAdd(mark.Value, entity.Id);
                }
            }
        }

        return index;
    }

    public string? DisplayNameOf(string moRef) =>
        _store.Current.Entities.TryGetValue(EntityId.For(_instanceId, moRef), out var entity)
            ? entity.DisplayName
            : null;

    private List<string> MoRefs(List<Entity> entities, EntityKind kind) =>
        [.. entities
            .Where(e => e.Kind == kind)
            .Select(e => e.Id.Value)
            .Where(id => id.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(id => id[Prefix.Length..])];
}
