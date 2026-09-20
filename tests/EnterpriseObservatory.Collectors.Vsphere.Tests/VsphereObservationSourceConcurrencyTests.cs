using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// What happens to the per-type probe caches when two reads overlap.
/// </summary>
/// <remarks>
/// <para>
/// One source instance serves every cycle — the registry hands back the same
/// object and the host holds it as a singleton — so its memory of what the
/// probe got wrong lives for the life of the process.
/// <c>IObservationSource.ReadAsync</c> is documented as non-re-entrant and
/// <c>SourceRunner</c> no longer retries a read it abandoned, but neither can
/// stop a read abandoned in one cycle from still running when the next starts
/// thirty seconds later. A .NET task cannot be aborted. So two reads can be in
/// here at once, and the caches have to survive it.
/// </para>
/// <para>
/// These tests use their own fakes rather than the ones in
/// <c>VsphereObservationSourceTests</c>, which record what they were asked into
/// plain <c>List</c>s. Driven from several threads those would be racing too,
/// and a test that cannot tell its own fake's corruption from the subject's
/// proves nothing.
/// </para>
/// </remarks>
public class VsphereObservationSourceConcurrencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// How many cycles the overlap is driven for.
    /// </summary>
    /// <remarks>
    /// The clock jumps past the re-check interval between rounds, so every
    /// round is a fresh decision about whether the hourly query is due — which
    /// is the decision the reads race over.
    /// </remarks>
    private const int Rounds = 300;

    private sealed class MovableClock : IClock
    {
        private long _ticks = T0.UtcTicks;

        public DateTimeOffset UtcNow => new(Volatile.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Volatile.Write(ref _ticks, UtcNow.Add(by).UtcTicks);
    }

    private sealed class Targets : IVsphereSampleTargetProvider
    {
        /// <remarks>
        /// No datastores. Datastore metrics are read per storage path rather
        /// than per datastore, so that type wants no counters of its own and
        /// never reaches the probe — including it would put a type in the
        /// targets that this test's arithmetic then has to except.
        /// </remarks>
        public VsphereSampleTargets Current { get; } = new()
        {
            Hosts = ["host-1"],
            VirtualMachines = ["vm-1"],
        };

        public EntityId? ResolveEntity(string moRef) => EntityId.For("vc-1", moRef);

        public EntityId? ResolveVolume(string volumeIdentifier) => null;

        public string? DisplayNameOf(string moRef) => moRef;
    }

    /// <summary>A vCenter whose probe admits to nothing and that returns no samples.</summary>
    /// <remarks>
    /// Chosen so the source stays on the branch that rations the hourly
    /// re-check for the whole run. Returning samples instead would prove the
    /// probe wrong on the first round and freeze the caches, and a frozen cache
    /// is not one this test can say anything about.
    /// </remarks>
    private sealed class SilentApi : IVsphereApi
    {
        private int _queries;

        public string InstanceId => "vc-1";

        /// <summary>How many performance queries actually went out.</summary>
        public int Queries => Volatile.Read(ref _queries);

        /// <remarks>
        /// Every counter the source asks for, so that it gets as far as the
        /// query. A catalogue missing them would have the source give up
        /// before the re-check decision this test is about.
        /// </remarks>
        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VsphereCounter>>(
                [.. VsphereCounters.Host
                    .Concat(VsphereCounters.VirtualMachine)
                    .Distinct(StringComparer.Ordinal)
                    .Select(Defined)]);

        private static VsphereCounter Defined(string key, int id)
        {
            var parts = key.Split('.');

            return new VsphereCounter
            {
                Id = id + 1,
                Group = parts[0],
                Name = parts[1],
                Rollup = VsphereCounter.ParseRollup(parts[2]),
                Unit = "percent",
                Level = 1,
            };
        }

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken ct) =>
            Task.FromResult<int?>(256);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string entityMoRef, VsphereEntityType entityType, DateTimeOffset nowUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
            IReadOnlyList<string> entityMoRefs,
            VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters,
            DateTimeOffset nowUtc,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _queries);
            return Task.FromResult<IReadOnlyList<PerfEntitySamples>>([]);
        }
    }

    [Fact]
    public async Task Overlapping_reads_do_not_corrupt_what_the_source_remembers_about_the_probe()
    {
        // Unguarded, the caches are a Dictionary and a HashSet written from
        // both reads, and they outlive every cycle: a lost entry at best, and
        // at worst a torn bucket array during a resize, surfacing as an
        // InvalidOperationException or a lookup that spins — inside the
        // collector whose job is to notice everything else going wrong. The
        // instance is reused until the process restarts, so the damage outlives
        // the cycle that caused it.
        //
        // Said plainly, because a green test is not evidence on its own: this
        // one does NOT fail against the unguarded caches, and it was run
        // against them to check. There are only ever two keys in these
        // collections, one per entity type that wants counters, so the
        // Dictionary never resizes and there is no bucket array to tear. The
        // corruption is real but needs an estate this fake does not have. What
        // this does catch is the cheap half — a reader seeing a cache another
        // is halfway through writing and reporting less than the truth — and
        // it pins the guarded behaviour so a future change cannot quietly drop
        // the guard and leave only the expensive half of the bug behind. The
        // test that does fail against the unguarded caches is the next one.
        var api = new SilentApi();
        var clock = new MovableClock();
        var source = new VsphereObservationSource(api, new Targets(), clock);

        var readers = Math.Max(4, Environment.ProcessorCount * 2);

        for (var round = 0; round < Rounds; round++)
        {
            // Past the hourly re-check, so every reader in this round arrives
            // at the decision believing the query is due, together.
            clock.Advance(TimeSpan.FromHours(2));

            var batches = await Task.WhenAll(
                Enumerable.Range(0, readers).Select(_ => Task.Run(() => source.ReadAsync(default))));

            foreach (var batch in batches)
            {
                // The probe said nothing for either type, so both are
                // reported as unmeasurable — every round, from every reader.
                // A count that drifts means a reader saw a cache another was
                // halfway through writing and silently reported less than the
                // truth, which is this product's one unforgivable failure.
                Assert.Equal(2, batch.Failures.Count(
                    f => f.Kind == CollectionFailureKind.NotConfigured));
                Assert.Empty(batch.Observations);
            }
        }
    }

    [Fact]
    public async Task Overlapping_reads_send_one_hourly_recheck_between_them_rather_than_one_each()
    {
        // The re-check exists to ration: the probe says a whole entity type has
        // no data, and rather than believing that forever the source tries once
        // an hour anyway. Deciding that with a read of the cache and then a
        // separate write to it means two overlapping reads both pass a check
        // the other is about to invalidate, and both query — so the rationing
        // silently becomes one query per overlapping read. On an estate where
        // the probe is wrong about every type, that is the monitoring tool
        // making work for the system it is monitoring, which is exactly what
        // the re-check interval was written to stop.
        var api = new SilentApi();
        var clock = new MovableClock();
        var source = new VsphereObservationSource(api, new Targets(), clock);

        var readers = Math.Max(4, Environment.ProcessorCount * 2);

        for (var round = 0; round < Rounds; round++)
        {
            clock.Advance(TimeSpan.FromHours(2));

            await Task.WhenAll(
                Enumerable.Range(0, readers).Select(_ => Task.Run(() => source.ReadAsync(default))));
        }

        // Two entity types, one re-check each per round, however many reads
        // were in flight when it came due.
        Assert.Equal(Rounds * 2, api.Queries);
    }
}
