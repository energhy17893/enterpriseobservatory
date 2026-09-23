using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Host.AllInOne.Collectors;
using EnterpriseObservatory.Host.AllInOne.State;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>The read-only port SimpliVity folds through (ADR-0027 §4), answered from the graph.</summary>
public class GraphFoldingDirectoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    private static Entity Of(string source, string moRef, EntityKind kind, string? uuid = null) => new()
    {
        Id = EntityId.For(source, moRef),
        Kind = kind,
        DisplayName = moRef,
        SourceInstanceId = source,
        LastSeenUtc = T0,
        Marks = uuid is null ? [] : [IdentityMark.Create(IdentityMarkKind.HardwareUuid, uuid, source)],
    };

    private static GraphFoldingDirectory Directory(params Entity[] entities)
    {
        var store = new InMemoryEntityGraphStore();
        store.Replace(EntityGraph.Empty.Merge(
            entities, [], [.. entities.Select(e => e.SourceInstanceId).Distinct()], T0, EntityRetentionPolicy.Default));

        return new GraphFoldingDirectory(store);
    }

    [Fact]
    public void A_vcenter_is_found_by_the_instance_uuid_it_is_marked_with()
    {
        var directory = Directory(
            Of("KibarHolding-KBVc01", "vcenter", EntityKind.VCenter, "5029A1B2-C3D4-4E5F-8A9B-0C1D2E3F4A5B"),
            Of("other-vc", "vcenter", EntityKind.VCenter, "11111111-2222-3333-4444-555555555555"),
            // A host's hardware UUID is not a vCenter's.
            Of("other-vc", "host-9", EntityKind.EsxiHost, "66666666-2222-3333-4444-555555555555"));

        Assert.Equal("KibarHolding-KBVc01", directory.VcenterFor("5029a1b2-c3d4-4e5f-8a9b-0c1d2e3f4a5b"));
        Assert.Null(directory.VcenterFor("66666666-2222-3333-4444-555555555555"));
        Assert.Null(directory.VcenterFor("00000000-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void Contains_and_vm_lookup_answer_from_the_current_graph()
    {
        var directory = Directory(
            Of("vc-1", "host-21", EntityKind.EsxiHost),
            Of("vc-1", "vm-101", EntityKind.VirtualMachine, "5012ABCD-0000-0000-0000-000000000101"));

        Assert.True(directory.Contains(EntityId.For("vc-1", "host-21")));
        Assert.False(directory.Contains(EntityId.For("vc-1", "host-22")));
        Assert.Equal(EntityId.For("vc-1", "vm-101"), directory.VirtualMachineByInstanceUuid("5012abcd-0000-0000-0000-000000000101"));
    }

    [Fact]
    public void An_esxi_host_is_found_by_its_hardware_uuid_in_any_case_and_only_a_host()
    {
        var directory = Directory(
            Of("vc-1", "host-4", EntityKind.EsxiHost, "31430760-2941-4f64-8973-44819448223c"),
            Of("vc-1", "vm-7", EntityKind.VirtualMachine, "77777777-2941-4f64-8973-44819448223c"));

        Assert.Equal(EntityId.For("vc-1", "host-4"), directory.HostByHardwareUuid("31430760-2941-4F64-8973-44819448223C"));
        Assert.Null(directory.HostByHardwareUuid("77777777-2941-4f64-8973-44819448223c"));
        Assert.Null(directory.HostBySerialNumber("DU62325C0N"));
    }
}
