using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Host.AllInOne.Collectors;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// What happens to the volume index when two reads are in the provider at once.
/// </summary>
/// <remarks>
/// <para>
/// They can be. The runner's timeout abandons a read rather than stopping it —
/// a .NET task cannot be aborted — so a read from the previous metric cycle can
/// still be running when the next one starts thirty seconds later, holding the
/// graph it read at the start. <c>IObservationSource.ReadAsync</c> says as
/// much.
/// </para>
/// <para>
/// This drives the real race rather than staging it, because the interleaving
/// that breaks the index is two instructions wide and nothing outside the
/// provider can suspend a thread inside it. The window is widened the only two
/// ways a test can widen it: many more samplers than the machine has cores, so
/// the scheduler has to deschedule them, and a graph replaced thousands of
/// times, so every sampler is driven through the rebuild at once, over and
/// over.
/// </para>
/// <para>
/// Both of those are load-bearing, and the numbers are worth keeping because
/// anyone tempted to trim them should know what they are trimming. Against the
/// unguarded provider: two samplers and a graph replaced as fast as possible
/// reproduced nothing in six runs; four samplers per core with 600
/// replacements reproduced it in six runs out of eight; the 4000 here
/// reproduced it in ten out of ten, between 45 and 620 stale lookups per run,
/// in about a second. A run that reports one stale lookup is the same defect as
/// a run that reports six hundred.
/// </para>
/// </remarks>
public class GraphSampleTargetProviderConcurrencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// How many times the inventory cycle replaces the graph.
    /// </summary>
    /// <remarks>
    /// Each replacement releases every sampler into the rebuild at the same
    /// moment, which is the only moment the race can happen — so this number,
    /// not elapsed time, is what the test is actually spending.
    /// </remarks>
    private const int Generations = 4_000;

    /// <summary>
    /// How many datastores a graph holds.
    /// </summary>
    /// <remarks>
    /// Bounded, and that is the whole reason this test is affordable. Letting
    /// the estate grow by one datastore per generation made every sampler
    /// rebuild an ever larger index on every replacement, and the run took
    /// minutes rather than the second it takes now. A generation's volume
    /// stays resolvable for this many generations afterwards, which is what
    /// lets a sampler tell a genuinely retired volume from a stale index — see
    /// the check in the loop.
    /// </remarks>
    private const int EstateSize = 64;

    [Fact]
    public async Task A_datastore_the_newest_inventory_found_still_resolves_while_reads_overlap()
    {
        // The failure this guards is not a crash, it is silence. If the index
        // and the graph it was built from are published as two separate
        // writes, an older read can stamp its own index with the newer read's
        // graph — and from then on every lookup finds the two equal and serves
        // the old index for as long as that graph stays current. A datastore
        // discovered this afternoon resolves to nothing, its latency samples
        // are dropped on the floor, and the dashboard shows a datastore with
        // no storage latency rather than a datastore with a problem. Missing
        // data that looks like healthy data is the one failure this product
        // exists to prevent.
        var store = new InMemoryEntityGraphStore();
        var provider = new GraphSampleTargetProvider(store, "vc-1");

        using var stop = new CancellationTokenSource();
        var published = 0;
        var stale = 0;

        // Replaces the graph wholesale, as the inventory cycle does. A
        // generation's datastore stays in the graph for EstateSize generations
        // and then retires, so the estate is a constant size. The generation is
        // announced only once its graph is in the store, so a sampler reading
        // it knows the store is at least that far along.
        var inventory = Task.Run(() =>
        {
            for (var generation = 1; generation <= Generations; generation++)
            {
                store.Replace(GraphOf(generation));
                Volatile.Write(ref published, generation);
                Thread.SpinWait(2_000);
            }

            stop.Cancel();
        });

        var samplers = Enumerable.Range(0, Environment.ProcessorCount * 4)
            .Select(_ => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var before = Volatile.Read(ref published);

                    if (before == 0)
                    {
                        continue;
                    }

                    var resolved = provider.ResolveVolume($"vol-{before}");
                    var after = Volatile.Read(ref published);

                    // Nothing found, and the estate did not turn over far
                    // enough during the call for the volume to have retired —
                    // so it was in the store's graph the whole time this call
                    // ran. A provider whose index matches its own graph always
                    // finds it. Finding nothing means an index built from an
                    // older graph is being served against a newer one.
                    if (resolved is null && after - before < EstateSize)
                    {
                        Interlocked.Increment(ref stale);
                    }
                }
            })).ToArray();

        await Task.WhenAll([inventory, .. samplers]);

        Assert.Equal(0, Volatile.Read(ref stale));
    }

    private static EntityGraph GraphOf(int generation) => EntityGraph.Empty with
    {
        Entities = Enumerable
            .Range(Math.Max(1, generation - EstateSize + 1), Math.Min(generation, EstateSize))
            .Select(Datastore)
            .ToDictionary(e => e.Id),
    };

    private static Entity Datastore(int n) => new()
    {
        Id = new EntityId($"vc-1:datastore-{n}"),
        Kind = EntityKind.Datastore,
        DisplayName = $"datastore-{n}",
        SourceInstanceId = "vc-1",
        Health = HealthState.Healthy,
        LastSeenUtc = T0,
        Marks = [IdentityMark.Create(IdentityMarkKind.VolumeIdentifier, $"vol-{n}", "vc-1")],
    };
}
