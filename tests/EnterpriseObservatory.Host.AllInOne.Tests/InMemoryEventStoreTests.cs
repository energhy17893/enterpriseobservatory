using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The in-memory event store keeps the Postgres store's reading rules, so that
/// tests composed on it see what production would.
/// </summary>
public class InMemoryEventStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static SourceEvent Event(long key, DateTimeOffset at, string type, string host = "host-1") => new()
    {
        Key = key,
        CreatedAtUtc = at,
        EventClass = "EventEx",
        TypeId = type,
        Message = type,
        Host = new EventObjectRef { MoRef = host, Name = host },
    };

    [Fact]
    public void Find_matches_type_ids_ignoring_case()
    {
        var store = new InMemoryEventStore();
        store.Record(
            "vc-1",
            [
                Event(1, T0.AddMinutes(-3), "com.vmware.vc.HA.DasHostIsolatedEvent"),
                Event(2, T0.AddMinutes(-2), "com.vmware.vc.ha.DasHostIsolatedEvent"),
            ],
            complete: true,
            T0);

        var found = store.Find("vc-1", ["COM.VMWARE.VC.HA.DASHOSTISOLATEDEVENT"], T0.AddHours(-1), T0);

        Assert.Equal([2L, 1L], found.Select(e => e.Key));
    }

    [Fact]
    public void Find_returns_at_most_the_matching_cap_newest_first()
    {
        var store = new InMemoryEventStore();
        var count = EventCollectionPipeline.MaxMatching + 5;
        store.Record(
            "vc-1",
            [.. Enumerable.Range(1, count).Select(i => Event(i, T0.AddSeconds(-count + i), "x"))],
            complete: true,
            T0);

        var found = store.Find("vc-1", ["x"], T0.AddDays(-1), T0);

        Assert.Equal(EventCollectionPipeline.MaxMatching, found.Count);
        Assert.Equal(count, found[0].Key);
        Assert.Equal(6, found[^1].Key);
    }

    [Fact]
    public void Of_types_keeps_every_kinds_newest_through_a_storm_of_another()
    {
        var store = new InMemoryEventStore();
        var isolation = Event(1, T0.AddHours(-20), "com.vmware.vc.HA.DasHostIsolatedEvent");
        var storm = Enumerable.Range(0, EventCollectionPipeline.MaxMatching + 10)
            .Select(i => Event(100 + i, T0.AddSeconds(-i), "esx.problem.net.redundancy.lost", "host-2"));

        store.Record("vc-1", [isolation, .. storm], complete: true, T0);

        var found = store.OfTypes(
            ["com.vmware.vc.ha.DasHostIsolatedEvent", "esx.problem.net.redundancy.lost"], T0.AddDays(-1));

        Assert.Equal(EventCollectionPipeline.MaxMatching, found.Count);
        Assert.Equal(100, found[0].Key);
        Assert.Equal(1, found[^1].Key);
    }
}
