using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Host.AllInOne.Collectors;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The join between the two cadences: discovery every few minutes, sampling
/// every few seconds against what discovery found.
/// </summary>
public class GraphSampleTargetProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryEntityGraphStore _store;

    public GraphSampleTargetProviderTests() => _store = new InMemoryEntityGraphStore();

    private GraphSampleTargetProvider Provider(string instanceId = "vc-1") => new(_store, instanceId);

    [Fact]
    public void Nothing_is_sampled_before_the_first_inventory_run()
    {
        // Correct rather than unfortunate: we would not know what to ask about.
        Assert.True(Provider().Current.IsEmpty);
    }

    [Fact]
    public void Hosts_virtual_machines_and_datastores_are_sampled_by_managed_object_reference()
    {
        Given(
            Entity("vc-1:host-1", EntityKind.EsxiHost),
            Entity("vc-1:vm-10", EntityKind.VirtualMachine),
            Entity("vc-1:datastore-5", EntityKind.Datastore));

        var targets = Provider().Current;

        Assert.Equal(["host-1"], targets.Hosts);
        Assert.Equal(["vm-10"], targets.VirtualMachines);
        Assert.Equal(["datastore-5"], targets.Datastores);
    }

    [Fact]
    public void Another_vcenters_entities_are_not_sampled_through_this_one()
    {
        // Managed object references are unique within a vCenter but not
        // between them, so asking one server about another's moRef would
        // silently return somebody else's numbers.
        Given(
            Entity("vc-1:host-1", EntityKind.EsxiHost),
            Entity("vc-2:host-1", EntityKind.EsxiHost, source: "vc-2"));

        Assert.Equal(["host-1"], Provider().Current.Hosts);
        Assert.Equal(["host-1"], Provider("vc-2").Current.Hosts);
    }

    [Fact]
    public void A_vanished_entity_is_not_sampled()
    {
        // It is retained so its history survives, but asking vCenter for
        // metrics on an object it no longer has produces one failure per cycle
        // forever.
        Given(Entity("vc-1:host-1", EntityKind.EsxiHost) with
        {
            ObservationState = ObservationState.Vanished,
        });

        Assert.Empty(Provider().Current.Hosts);
    }

    [Fact]
    public void An_entity_in_maintenance_is_still_sampled()
    {
        // Maintenance suppresses notification, not observation. A host being
        // patched still produces real numbers, and dropping them leaves a hole
        // in the history exactly where someone will later want to look.
        Given(Entity("vc-1:host-1", EntityKind.EsxiHost) with
        {
            ObservationState = ObservationState.InMaintenance,
        });

        Assert.Equal(["host-1"], Provider().Current.Hosts);
    }

    [Fact]
    public void A_sample_for_something_we_do_not_know_about_resolves_to_nothing()
    {
        // A measurement with no subject is worse than no measurement: it looks
        // like data.
        Given(Entity("vc-1:host-1", EntityKind.EsxiHost));

        Assert.Equal(new EntityId("vc-1:host-1"), Provider().ResolveEntity("host-1"));
        Assert.Null(Provider().ResolveEntity("host-99"));
    }

    // Placed directly rather than merged: a merge would re-decide the
    // observation state from the fact that it was just observed, which is
    // exactly the state these tests are setting.
    private void Given(params Entity[] entities) =>
        _store.Replace(EntityGraph.Empty with { Entities = entities.ToDictionary(e => e.Id) });

    private static Entity Entity(
        string id,
        EntityKind kind,
        string source = "vc-1",
        string? volume = null,
        string? display = null) => new()
    {
        Id = new EntityId(id),
        Kind = kind,
        DisplayName = display ?? id,
        SourceInstanceId = source,
        Health = HealthState.Healthy,
        LastSeenUtc = T0,
        Marks = volume is null
            ? []
            : [IdentityMark.Create(IdentityMarkKind.VolumeIdentifier, volume, source)],
    };

    // --- volume resolution ------------------------------------------------

    [Fact]
    public void A_volume_identifier_resolves_to_the_datastore_that_owns_it()
    {
        // vSphere measures datastore latency on each host and names the volume
        // in the counter instance, so this lookup is the only thing that turns
        // a number measured on a host into a number about a datastore.
        Given(
            Entity("vc-1:datastore-5", EntityKind.Datastore, volume: "608bd301-3f719074"),
            Entity("vc-1:datastore-6", EntityKind.Datastore, volume: "aaaabbbb-ccccdddd"));

        Assert.Equal(
            new EntityId("vc-1:datastore-5"),
            Provider().ResolveVolume("608bd301-3f719074"));
    }

    [Fact]
    public void An_unknown_volume_resolves_to_nothing_rather_than_to_something_nearby()
    {
        // A datastore mounted on a host but not collected, or added since the
        // last inventory cycle. A measurement attached to a guess looks like
        // knowledge, which is worse than a measurement that was dropped.
        Given(Entity("vc-1:datastore-5", EntityKind.Datastore, volume: "608bd301-3f719074"));

        Assert.Null(Provider().ResolveVolume("no-such-volume"));
        Assert.Null(Provider().ResolveVolume(""));
    }

    [Fact]
    public void Another_vcenters_volume_is_not_resolved_through_this_one()
    {
        // The same reason moRefs are qualified: two vCenters may both mount a
        // volume, and attributing one's measurement to the other's datastore
        // would be silently wrong on exactly the estates where it matters.
        Given(Entity("vc-2:datastore-5", EntityKind.Datastore, source: "vc-2", volume: "shared-vol"));

        Assert.Null(Provider().ResolveVolume("shared-vol"));
        Assert.Equal(new EntityId("vc-2:datastore-5"), Provider("vc-2").ResolveVolume("shared-vol"));
    }

    [Fact]
    public void A_datastore_discovered_after_the_first_lookup_is_still_found()
    {
        // The index is rebuilt when the graph is replaced, which is the only
        // way it changes. Caching without that would mean a datastore added
        // this afternoon has its latency land nowhere, silently, until the
        // service is restarted.
        var provider = Provider();

        Given(Entity("vc-1:datastore-5", EntityKind.Datastore, volume: "first-vol"));
        Assert.Null(provider.ResolveVolume("second-vol"));

        Given(
            Entity("vc-1:datastore-5", EntityKind.Datastore, volume: "first-vol"),
            Entity("vc-1:datastore-6", EntityKind.Datastore, volume: "second-vol"));

        Assert.Equal(new EntityId("vc-1:datastore-6"), provider.ResolveVolume("second-vol"));
        Assert.Equal(new EntityId("vc-1:datastore-5"), provider.ResolveVolume("first-vol"));
    }

    [Fact]
    public void A_volume_identifier_matches_regardless_of_case()
    {
        // vSphere spells a VMFS UUID in lower case in summary.url and marks
        // are normalised the same way, but an NFS identifier is whatever the
        // server said. Matching case-sensitively would drop those silently.
        Given(Entity("vc-1:datastore-5", EntityKind.Datastore, volume: "AbCd-1234"));

        Assert.Equal(new EntityId("vc-1:datastore-5"), Provider().ResolveVolume("abcd-1234"));
    }

    [Fact]
    public void A_host_is_named_so_a_datastore_series_can_say_who_measured_it()
    {
        // One datastore carries one series per host, and the instance is the
        // host's name. An unnamed one would merge two hosts' storage paths
        // into a single line.
        Given(Entity("vc-1:host-1", EntityKind.EsxiHost, display: "esx01.corp.local"));

        Assert.Equal("esx01.corp.local", Provider().DisplayNameOf("host-1"));
        Assert.Null(Provider().DisplayNameOf("host-404"));
    }
}
