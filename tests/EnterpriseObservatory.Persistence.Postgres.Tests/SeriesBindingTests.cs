using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// Whether a cached series id may be used, without a database.
/// </summary>
/// <remarks>
/// <para>
/// The same argument <c>CompactionOrderingTests</c> makes. The SQL that
/// confirms an id belongs against a real server; the rule for what to do with
/// the answer is C#, is wrong silently, and is wrong in a way that costs an
/// entire cycle's samples for the whole estate — so it is checked here, where
/// it runs on every machine rather than only on one that has PostgreSQL and a
/// password for it.
/// </para>
/// <para>
/// What none of this proves is that the <c>FOR KEY SHARE</c> statement really
/// takes the lock. That is the server's behaviour and only a server can answer
/// for it.
/// </para>
/// </remarks>
public class SeriesBindingTests
{
    private static SeriesKey Key(string name) =>
        new(EntityId.For("vc-1", name), "cpu.usage.average", string.Empty);

    [Fact]
    public void A_cached_id_the_database_did_not_confirm_is_resolved_again()
    {
        // The defect itself. Compaction deletes a decommissioned machine's
        // empty series in its own transaction while the metric cycle is already
        // holding that id; sample.series_id is a foreign key, so writing
        // against the dead id aborts the append. Nothing commits — the cycle's
        // samples for every other entity go too, and the estate has a
        // thirty-second hole in its history. Treat an unconfirmed id as usable
        // and that is exactly what happens.
        var key = Key("decommissioned");

        var needed = SeriesBinding.NeedingResolution(
            [key],
            new Dictionary<SeriesKey, long> { [key] = 7 },
            new HashSet<long>());

        Assert.Equal([key], needed);
    }

    [Fact]
    public void A_cached_id_the_database_confirmed_is_not_resolved_again()
    {
        // The reason this is affordable. Six thousand series are appended every
        // thirty seconds; re-upserting the ones the database has just confirmed
        // would write six thousand new row versions a cycle for no change at
        // all, and leave autovacuum to clean up after a fix that was supposed
        // to cost one round trip.
        var key = Key("live");

        var needed = SeriesBinding.NeedingResolution(
            [key],
            new Dictionary<SeriesKey, long> { [key] = 7 },
            new HashSet<long> { 7 });

        Assert.Empty(needed);
    }

    [Fact]
    public void A_series_that_has_never_been_seen_is_resolved()
    {
        // A counter's first ever appearance, which is every counter on the
        // first cycle after a new virtual machine is inventoried. Miss it and
        // the append has no id to write against at all.
        var key = Key("brand-new");

        var needed = SeriesBinding.NeedingResolution(
            [key], new Dictionary<SeriesKey, long>(), new HashSet<long>());

        Assert.Equal([key], needed);
    }

    [Fact]
    public void A_cached_id_is_matched_to_its_own_key_and_not_to_another()
    {
        // Two series, one confirmed and one not. Compare the wrong pairs and
        // the confirmed series gets pointlessly rewritten while the deleted one
        // is passed through to the foreign key, which is both halves of the
        // bug at once.
        var live = Key("live");
        var gone = Key("gone");

        var needed = SeriesBinding.NeedingResolution(
            [live, gone],
            new Dictionary<SeriesKey, long> { [live] = 1, [gone] = 2 },
            new HashSet<long> { 1 });

        Assert.Equal([gone], needed);
    }

    [Fact]
    public void A_series_appearing_several_times_in_one_batch_is_resolved_once()
    {
        // vCenter returns several samples per query and consecutive cycles
        // overlap, so the same series arrives repeatedly in one batch. These
        // keys become a single ON CONFLICT statement, and PostgreSQL rejects
        // outright one that would touch the same row twice — so a duplicate
        // here does not cost efficiency, it fails the whole append.
        var key = Key("repeated");

        var needed = SeriesBinding.NeedingResolution(
            [key, key, key], new Dictionary<SeriesKey, long>(), new HashSet<long>());

        Assert.Equal([key], needed);
    }

    [Fact]
    public void Only_ids_that_are_actually_cached_are_asked_about()
    {
        // The confirming statement is a lookup by primary key over the ids the
        // cache offered. Inventing an entry for a series with no cached id
        // would ask the database about an id nobody holds, and — worse — a
        // default of zero would be confirmed as absent and look like ordinary
        // staleness rather than a bug.
        var cached = Key("cached");
        var uncached = Key("uncached");

        var ids = SeriesBinding.HintedIds(
            [cached, uncached], new Dictionary<SeriesKey, long> { [cached] = 42 });

        Assert.Equal([42L], ids);
    }

    [Fact]
    public void The_same_id_is_only_asked_about_once()
    {
        // The array goes to the server on every cycle. Repeating the same id
        // once per sample would send tens of thousands of entries where six
        // thousand were meant, on the one path this adapter uses COPY to keep
        // cheap.
        var key = Key("repeated");

        var ids = SeriesBinding.HintedIds(
            [key, key], new Dictionary<SeriesKey, long> { [key] = 42 });

        Assert.Equal([42L], ids);
    }
}

/// <summary>
/// The same defect against a real server, end to end.
/// </summary>
/// <remarks>
/// <para>
/// The interleaving without the threads. Two stores over one database are in
/// precisely the state a single racing store is in mid-append: one of them
/// holds a cached id, the other has already deleted the row it names. That is
/// the window, reproduced deterministically — a test that started two threads
/// and hoped they collided would pass on a quiet machine whatever the code
/// did.
/// </para>
/// <para>
/// Skips without <c>EO_TEST_PG_PASSWORD</c>, which means it has never run on
/// the machine the fix was written on. It is the only thing that can prove the
/// foreign key no longer fires, so it is worth saying plainly that what proves
/// it is CI.
/// </para>
/// </remarks>
public class SeriesLifetimeTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Observation Sample(EntityId entity, DateTimeOffset at, double value) => new()
    {
        Entity = entity,
        Source = "vc-1",
        SampledAtUtc = at,
        Value = new CounterValue
        {
            CounterName = "cpu.usage.average",
            Raw = value,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(30),
            Unit = "percent",
        },
    };

    private static SeriesResult Read(
        PostgresObservationStore store, EntityId entity, DateTimeOffset from, DateTimeOffset to) =>
        store.Query(new SeriesQuery
        {
            Key = new SeriesKey(entity, "cpu.usage.average", string.Empty),
            FromUtc = from,
            ToUtc = to,
            Resolution = SeriesResolution.Raw,
            MaxPoints = 100,
        });

    [SkippableFact]
    public void An_append_survives_the_series_it_cached_being_swept_away_underneath_it()
    {
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

        // The whole point. The tidied-up series belongs to a decommissioned
        // machine nobody is looking at; the second entity is the rest of the
        // estate, which is what the old code actually lost. The append aborted
        // on the foreign key, PostgresDatabase.Write never committed, and every
        // entity's samples for that cycle went with it.
        var writer = new PostgresObservationStore(_live.Database);

        var retired = EntityId.For("vc-1", "retired-vm");
        var rest = EntityId.For("vc-1", "the-rest-of-the-estate");

        writer.Append([Sample(retired, T0, 1)]);

        // The row goes behind the writer's back, which is the only thing that
        // matters here. Compaction used to be what did it; it no longer deletes
        // series rows at all (ADR-0019), and the guard still has to hold —
        // restoring a database dump, an operator pruning by hand and any sweep
        // a later release adds all leave the cache naming a row that is gone.
        // So the deletion is made directly rather than borrowed from a caller
        // that might stop doing it again.
        Delete(retired);
        Assert.Empty(new PostgresObservationStore(_live.Database).SeriesFor(retired));

        var later = T0.AddDays(500);
        writer.Append([Sample(retired, later, 2), Sample(rest, later, 3)]);

        Assert.Single(Read(writer, retired, later, later.AddMinutes(1)).Points);
        Assert.Single(Read(writer, rest, later, later.AddMinutes(1)).Points);
    }

    /// <summary>Removes one entity's series rows, as something outside this store would.</summary>
    private void Delete(EntityId entity) => _live.Database.Write(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM series WHERE entity_id = @entity;";
        command.Parameters.AddWithValue("entity", entity.Value);
        command.ExecuteNonQuery();
    });
}
