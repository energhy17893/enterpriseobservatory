using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Host.AllInOne.Collectors;
using EnterpriseObservatory.Persistence.Sqlite;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The join between the two cadences: discovery every few minutes, sampling
/// every few seconds against what discovery found.
/// </summary>
public class GraphSampleTargetProviderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly ObservatoryDatabase _database = new(new SqliteStoreOptions
    {
        Path = string.Empty,
        InMemory = true,
    });

    private readonly SqliteEntityGraphStore _store;

    public GraphSampleTargetProviderTests() => _store = new SqliteEntityGraphStore(_database);

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

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

    private static Entity Entity(string id, EntityKind kind, string source = "vc-1") => new()
    {
        Id = new EntityId(id),
        Kind = kind,
        DisplayName = id,
        SourceInstanceId = source,
        Health = HealthState.Healthy,
        LastSeenUtc = T0,
    };
}
