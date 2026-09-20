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

    /// <summary>
    /// Finds the datastore a volume identifier belongs to.
    /// </summary>
    /// <remarks>
    /// Built per call from the current graph rather than cached. The lookup is
    /// over a few dozen datastores and runs once per metric cycle, while a
    /// cache would have to be invalidated by the inventory cycle — and the
    /// failure it would produce is a datastore added this afternoon whose
    /// latency silently lands nowhere.
    /// </remarks>
    public EntityId? ResolveVolume(string volumeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(volumeIdentifier))
        {
            return null;
        }

        foreach (var entity in _store.Current.Entities.Values)
        {
            if (entity.Kind != EntityKind.Datastore ||
                !string.Equals(entity.SourceInstanceId, _instanceId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var mark in entity.Marks)
            {
                if (mark.Kind == IdentityMarkKind.VolumeIdentifier &&
                    string.Equals(mark.Value, volumeIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    return entity.Id;
                }
            }
        }

        return null;
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
