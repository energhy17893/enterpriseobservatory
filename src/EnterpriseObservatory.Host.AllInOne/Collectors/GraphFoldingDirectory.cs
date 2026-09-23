using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Collectors.Redfish;
using EnterpriseObservatory.Collectors.Simplivity;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Host.AllInOne.Collectors;

/// <summary>
/// Answers the SimpliVity source's folding questions from the entity graph
/// (ADR-0027 §4) — the <see cref="GraphSampleTargetProvider"/> pattern: the
/// collector asks, the host reads the graph, the collector never does.
/// </summary>
/// <remarks>
/// The vCenter is found by the <c>about.instanceUuid</c> its own collector
/// marks it with (PR #140), a VM by the instance UUID mark vSphere gives it.
/// Indexed once per graph, since one read asks about every host and VM.
/// </remarks>
public sealed class GraphFoldingDirectory(IEntityGraphStore store) : ISimplivityFoldingDirectory, IRedfishFoldingDirectory
{
    private readonly IEntityGraphStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private sealed record Index(
        EntityGraph Graph,
        Dictionary<string, string> Vcenters,
        Dictionary<string, EntityId> VirtualMachines,
        Dictionary<(IdentityMarkKind, string), EntityId> Hosts);

    private Index? _index;

    private readonly Lock _padlock = new();

    public string? VcenterFor(string instanceUuid) =>
        Current().Vcenters.GetValueOrDefault(instanceUuid.Trim().ToLowerInvariant());

    public bool Contains(EntityId entity) =>
        _store.Current.Entities.TryGetValue(entity, out var e) && e.ObservationState != ObservationState.Vanished;

    public EntityId? VirtualMachineByInstanceUuid(string instanceUuid) =>
        Current().VirtualMachines.TryGetValue(instanceUuid.Trim().ToLowerInvariant(), out var id) ? id : null;

    /// <summary>The ESXi host whose <c>hardware.systemInfo.uuid</c> mark this is (Redfish, M6.1).</summary>
    public EntityId? HostByHardwareUuid(string uuid) => Host(IdentityMarkKind.HardwareUuid, uuid);

    /// <summary>The ESXi host with this serial mark. vSphere emits none today, so this finds nothing until it does.</summary>
    public EntityId? HostBySerialNumber(string serial) => Host(IdentityMarkKind.SerialNumber, serial);

    private EntityId? Host(IdentityMarkKind kind, string value) =>
        Current().Hosts.TryGetValue((kind, value.Trim().ToLowerInvariant()), out var id) ? id : null;

    private Index Current()
    {
        var graph = _store.Current;

        lock (_padlock)
        {
            if (_index is { } index && ReferenceEquals(index.Graph, graph))
            {
                return index;
            }

            var vcenters = new Dictionary<string, string>(StringComparer.Ordinal);
            var vms = new Dictionary<string, EntityId>(StringComparer.Ordinal);
            var hosts = new Dictionary<(IdentityMarkKind, string), EntityId>();

            foreach (var entity in graph.Active)
            {
                if (entity.Kind == EntityKind.EsxiHost)
                {
                    foreach (var mark in entity.Marks.Where(m => m.Kind is IdentityMarkKind.HardwareUuid or IdentityMarkKind.SerialNumber))
                    {
                        hosts.TryAdd((mark.Kind, mark.Value), entity.Id);
                    }
                }

                foreach (var mark in entity.Marks.Where(m => m.Kind == IdentityMarkKind.HardwareUuid))
                {
                    if (entity.Kind == EntityKind.VCenter)
                    {
                        vcenters.TryAdd(mark.Value, entity.SourceInstanceId);
                    }
                    else if (entity.Kind == EntityKind.VirtualMachine)
                    {
                        vms.TryAdd(mark.Value, entity.Id);
                    }
                }
            }

            _index = new Index(graph, vcenters, vms, hosts);
            return _index;
        }
    }
}
