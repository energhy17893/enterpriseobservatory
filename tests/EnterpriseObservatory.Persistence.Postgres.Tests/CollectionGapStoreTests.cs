using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// The source-level gap record (migration 13), against a real server.
/// </summary>
public class CollectionGapStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private static CollectionGap Gap(string source, DateTimeOffset from, DateTimeOffset to) => new()
    {
        SourceInstanceId = source,
        FromUtc = from,
        ToUtc = to,
        FilledToUtc = from,
        State = CollectionGapState.Open,
        OpenedAtUtc = T0,
    };

    [SkippableFact]
    public void Open_gaps_come_back_oldest_first_and_several_may_be_open_at_once()
    {
        RequireDatabase();

        var store = new PostgresCollectionGapStore(_live.Database);

        // No unique constraint: a restart during a fill leaves the first gap
        // open and records a second one.
        var later = store.Open(Gap("vc-1", T0.AddMinutes(-20), T0.AddMinutes(-2)));
        var earlier = store.Open(Gap("vc-1", T0.AddMinutes(-90), T0.AddMinutes(-40)));
        store.Open(Gap("vc-2", T0.AddMinutes(-90), T0.AddMinutes(-40)));

        Assert.NotEqual(later.Id, earlier.Id);

        var open = store.OpenGaps("vc-1");

        Assert.Equal([earlier.Id, later.Id], open.Select(g => g.Id));
        Assert.Equal(T0.AddMinutes(-90), open[0].FromUtc);
        Assert.Equal(T0.AddMinutes(-40), open[0].ToUtc);
        Assert.Equal(CollectionGapState.Open, open[0].State);
    }

    [SkippableFact]
    public void Progress_and_closing_are_kept_and_a_closed_gap_leaves_the_open_list_but_not_the_record()
    {
        RequireDatabase();

        var store = new PostgresCollectionGapStore(_live.Database);
        var gap = store.Open(Gap("vc-1", T0.AddMinutes(-90), T0.AddMinutes(-2)));

        var expired = CollectionGaps.Expire(gap, T0.AddMinutes(-59), T0);
        store.Update(expired);

        var done = CollectionGaps.Advance(expired, T0.AddMinutes(-2), T0.AddMinutes(1));
        store.Update(done);

        Assert.Empty(store.OpenGaps("vc-1"));

        var kept = Assert.Single(new PostgresCollectionGapStore(_live.Database).Gaps("vc-1"));
        Assert.Equal(CollectionGapState.Unrecoverable, kept.State);
        Assert.Equal(T0.AddMinutes(-59), kept.LostBeforeUtc);
        Assert.Equal(T0.AddMinutes(-2), kept.FilledToUtc);
        Assert.Equal(T0.AddMinutes(1), kept.ClosedAtUtc);
    }

    [SkippableFact]
    public void An_update_never_moves_a_gap_back_or_reopens_it()
    {
        RequireDatabase();

        var store = new PostgresCollectionGapStore(_live.Database);
        var gap = store.Open(Gap("vc-1", T0.AddMinutes(-30), T0.AddMinutes(-2)));

        var filled = CollectionGaps.Advance(gap, T0.AddMinutes(-2), T0);
        store.Update(filled);

        // A stale write from an overlapping read: behind, and still open.
        store.Update(gap with { FilledToUtc = T0.AddMinutes(-20) });

        var kept = Assert.Single(store.Gaps("vc-1"));
        Assert.Equal(CollectionGapState.Filled, kept.State);
        Assert.Equal(T0.AddMinutes(-2), kept.FilledToUtc);
    }

    [SkippableFact]
    public void The_database_refuses_a_fill_point_outside_the_gap()
    {
        RequireDatabase();

        var store = new PostgresCollectionGapStore(_live.Database);
        var gap = store.Open(Gap("vc-1", T0.AddMinutes(-30), T0.AddMinutes(-2)));

        Assert.ThrowsAny<PostgresException>(() =>
            store.Update(gap with { FilledToUtc = T0.AddMinutes(5) }));
    }

    [SkippableFact]
    public void Counts_by_state_span_every_source_for_the_health_endpoint()
    {
        RequireDatabase();

        var store = new PostgresCollectionGapStore(_live.Database);

        // One open gap each on two different sources, and one closed
        // unrecoverable -- /health (Package D) reads the totals across all
        // sources, not per source.
        store.Open(Gap("vc-1", T0.AddMinutes(-30), T0.AddMinutes(-2)));
        store.Open(Gap("vc-2", T0.AddMinutes(-30), T0.AddMinutes(-2)));

        var lost = store.Open(Gap("vc-3", T0.AddMinutes(-90), T0.AddMinutes(-2)));
        var expired = CollectionGaps.Expire(lost, T0.AddMinutes(-59), T0);
        store.Update(expired);
        store.Update(CollectionGaps.Advance(expired, T0.AddMinutes(-2), T0.AddMinutes(1)));

        var counts = store.CountsByState();

        Assert.Equal(2, counts[CollectionGapState.Open]);
        Assert.Equal(1, counts[CollectionGapState.Unrecoverable]);
    }

    [SkippableFact]
    public void The_newest_stored_sample_of_each_entity_is_its_mark()
    {
        RequireDatabase();

        var samples = new PostgresObservationStore(_live.Database);
        var host = EntityId.For("vc-1", "host-1");
        var vm = EntityId.For("vc-1", "vm-1");

        samples.Append(
        [
            Sample(host, "cpu.usage.average", T0.AddMinutes(-30)),
            Sample(host, "cpu.usage.average", T0.AddMinutes(-31)),
            Sample(host, "mem.usage.average", T0.AddMinutes(-29)),
            Sample(vm, "cpu.usage.average", T0.AddMinutes(-45)),
        ]);

        var marks = new PostgresCollectionGapStore(_live.Database)
            .LatestSampleTimes([host, vm, EntityId.For("vc-1", "never")]);

        Assert.Equal(2, marks.Count);
        Assert.Equal(T0.AddMinutes(-29), marks[host]);
        Assert.Equal(T0.AddMinutes(-45), marks[vm]);
    }

    private static Observation Sample(EntityId entity, string counter, DateTimeOffset at) => new()
    {
        Entity = entity,
        Value = new CounterValue
        {
            CounterName = counter,
            Raw = 1,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "%",
        },
        SampledAtUtc = at,
        Source = "vc-1",
    };
}
