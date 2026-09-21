using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// Collected events and their cursors, against a real server.
/// </summary>
/// <remarks>
/// The claims here are the ones a fake would make about itself: that a
/// duplicate is ignored by the conflict clause rather than stored twice, that
/// the mark and the events commit together and survive a restart, and that
/// retention deletes by age and nothing else.
/// </remarks>
public class EventStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private static SourceEvent Event(long key, DateTimeOffset at) => new()
    {
        Key = key,
        ChainId = key,
        CreatedAtUtc = at,
        EventClass = "EventEx",
        TypeId = "esx.problem.storage.connectivity.lost",
        Severity = "error",
        Message = $"event {key}",
        UserName = null,
        DatacenterName = "DC1",
        Host = new EventObjectRef { MoRef = "host-42", Name = "esx01.corp.local" },
        Datastore = new EventObjectRef { MoRef = "datastore-11", Name = "ds-gold-01" },
    };

    [SkippableFact]
    public void Events_and_the_mark_survive_a_restart_as_they_went_in()
    {
        RequireDatabase();

        new PostgresEventStore(_live.Database).Record(
            "vc-1", [Event(1, T0.AddMinutes(-2)), Event(2, T0.AddMinutes(-1))], complete: true, T0);

        _live.Restart();

        var store = new PostgresEventStore(_live.Database);
        var cursor = Assert.Single(store.Cursors);

        Assert.Equal(2, cursor.Mark?.Key);
        Assert.Equal(T0.AddMinutes(-1), cursor.Mark?.CreatedAtUtc);
        Assert.Equal(T0, cursor.LastSuccessUtc);

        var newest = store.Recent(10)[0];
        Assert.Equal(2, newest.Key);
        Assert.Equal("vc-1", newest.SourceInstanceId);
        Assert.Equal("esx.problem.storage.connectivity.lost", newest.TypeId);
        Assert.Equal("EventEx", newest.EventClass);
        Assert.Equal("host-42", newest.Host?.MoRef);
        Assert.Equal("ds-gold-01", newest.Datastore?.Name);
        Assert.Null(newest.VirtualMachine);
        Assert.Null(newest.UserName);
    }

    [SkippableFact]
    public void An_overlapping_read_does_not_store_an_event_twice()
    {
        // Reads overlap on purpose, so the conflict clause is on the path of
        // every cycle. Without it the second read would fail the transaction.
        RequireDatabase();

        var store = new PostgresEventStore(_live.Database);

        store.Record("vc-1", [Event(1, T0)], complete: true, T0);
        store.Record("vc-1", [Event(1, T0), Event(2, T0.AddSeconds(1))], complete: true, T0.AddMinutes(5));

        Assert.Equal(2, store.Recent(10).Count);
    }

    [SkippableFact]
    public void A_rebuilt_vCenter_reusing_a_key_is_not_mistaken_for_a_duplicate()
    {
        RequireDatabase();

        var store = new PostgresEventStore(_live.Database);

        store.Record("vc-1", [Event(7, T0.AddDays(-3))], complete: true, T0.AddDays(-3));
        store.Record("vc-1", [Event(7, T0)], complete: true, T0);

        Assert.Equal(2, store.Recent(10).Count);
    }

    [SkippableFact]
    public void A_failure_keeps_the_mark_and_is_remembered()
    {
        RequireDatabase();

        var store = new PostgresEventStore(_live.Database);

        store.Record("vc-1", [Event(3, T0)], complete: true, T0);
        store.RecordFailure("vc-1", "vCenter did not answer", T0.AddMinutes(5));

        _live.Restart();

        var cursor = Assert.Single(new PostgresEventStore(_live.Database).Cursors);

        Assert.Equal(3, cursor.Mark?.Key);
        Assert.Equal(T0, cursor.LastSuccessUtc);
        Assert.Equal(T0.AddMinutes(5), cursor.LastAttemptUtc);
        Assert.Equal("vCenter did not answer", cursor.LastFailure);
    }

    [SkippableFact]
    public void A_source_that_has_only_ever_failed_still_has_a_cursor()
    {
        // "Could not read" has to be sayable, and an absent row cannot say it.
        RequireDatabase();

        var store = new PostgresEventStore(_live.Database);

        store.RecordFailure("vc-2", "no event manager", T0);

        var cursor = Assert.Single(store.Cursors);
        Assert.Null(cursor.Mark);
        Assert.Null(cursor.LastSuccessUtc);
        Assert.Equal("no event manager", cursor.LastFailure);
    }

    [SkippableFact]
    public void A_quiet_read_moves_the_time_but_not_the_mark()
    {
        RequireDatabase();

        var store = new PostgresEventStore(_live.Database);

        store.Record("vc-1", [Event(3, T0)], complete: true, T0);
        store.Record("vc-1", [], complete: true, T0.AddMinutes(5));

        var cursor = Assert.Single(store.Cursors);
        Assert.Equal(3, cursor.Mark?.Key);
        Assert.Equal(T0.AddMinutes(5), cursor.LastSuccessUtc);
    }

    [SkippableFact]
    public void Retention_removes_old_events_and_leaves_the_rest()
    {
        RequireDatabase();

        var store = new PostgresEventStore(_live.Database);

        store.Record("vc-1", [Event(1, T0.AddDays(-40)), Event(2, T0.AddDays(-1))], complete: true, T0);

        var removed = store.Prune(T0 - EventCollectionPipeline.Retention);

        Assert.Equal(1, removed);
        Assert.Equal(2, Assert.Single(store.Recent(10)).Key);

        // The cursor is not an event and is not aged out with them.
        Assert.Equal(2, Assert.Single(store.Cursors).Mark?.Key);
    }

    [SkippableFact]
    public void Recent_is_newest_first_and_can_be_narrowed_to_one_source()
    {
        RequireDatabase();

        var store = new PostgresEventStore(_live.Database);

        store.Record("vc-1", [Event(1, T0.AddMinutes(-3)), Event(2, T0.AddMinutes(-1))], complete: true, T0);
        store.Record("vc-2", [Event(9, T0.AddMinutes(-2))], complete: true, T0);

        Assert.Equal([2L, 9L, 1L], store.Recent(10).Select(e => e.Key));
        Assert.Equal([9L], store.Recent(10, "vc-2").Select(e => e.Key));
        Assert.Single(store.Recent(1));
    }
}
