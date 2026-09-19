using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

public class VsphereObservationSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
    }

    private sealed class Targets(params string[] hosts) : IVsphereSampleTargetProvider
    {
        public VsphereSampleTargets Current { get; } = new() { Hosts = hosts };

        /// <summary>Unknown refs resolve to null, as an unmapped entity would.</summary>
        public HashSet<string> Resolvable { get; } = [.. hosts];

        public EntityId? ResolveEntity(string moRef) =>
            Resolvable.Contains(moRef) ? new EntityId(moRef) : null;
    }

    private sealed class FakeApi : IVsphereApi
    {
        public string InstanceId => "vc-1";

        public int? MaxQueryMetrics { get; init; } = 256;

        public List<VsphereCounter> Catalog { get; init; } = [.. AllHostCounters()];

        /// <summary>What the platform is actually collecting. Defaults to everything.</summary>
        public HashSet<string>? Available { get; init; }

        /// <summary>A server that answers the query but has nothing to give.</summary>
        /// <remarks>
        /// The case that separates "the probe was wrong" from "the probe was
        /// right": both return a query, only one returns data.
        /// </remarks>
        public bool EmptySamples { get; init; }

        /// <summary>Batch sizes the server refuses, to exercise adaptive sizing.</summary>
        public int RefuseBatchesLargerThan { get; init; } = int.MaxValue;

        public List<int> ObservedBatchSizes { get; } = [];

        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VsphereCounter>>(Catalog);

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken ct) =>
            Task.FromResult(MaxQueryMetrics);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string entityMoRef, VsphereEntityType entityType, DateTimeOffset nowUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                [.. Available ?? [.. Catalog.Select(c => c.Key)]]);

        /// <summary>The window each query asked for, or null for real-time.</summary>
        /// <remarks>
        /// Recorded because its absence was the defect: a historical query
        /// with no range comes back empty, and empty is not an error.
        /// </remarks>
        public List<(DateTimeOffset Now, VsphereEntityType Type)> ObservedQueries { get; } = [];

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
            IReadOnlyList<string> entityMoRefs,
            VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters,
            DateTimeOffset nowUtc,
            CancellationToken ct)
        {
            ObservedBatchSizes.Add(entityMoRefs.Count);
            ObservedQueries.Add((nowUtc, entityType));

            if (entityMoRefs.Count > RefuseBatchesLargerThan)
            {
                throw new VsphereQuerySizeRefusedException("Request processing is restricted by administrator.");
            }

            if (EmptySamples)
            {
                return Task.FromResult<IReadOnlyList<PerfEntitySamples>>([]);
            }

            return Task.FromResult<IReadOnlyList<PerfEntitySamples>>(
            [
                .. entityMoRefs.Select(moRef => new PerfEntitySamples
                {
                    EntityMoRef = moRef,
                    Values = [.. counters.Select(c => new CounterValue
                    {
                        CounterName = c.Key,
                        Raw = 42,
                        Rollup = c.Rollup,
                        Interval = TimeSpan.FromSeconds(20),
                        Unit = c.Unit,
                    })],
                }),
            ]);
        }

        private static IEnumerable<VsphereCounter> AllHostCounters() =>
            VsphereCounters.Host.Select((key, index) =>
            {
                var parts = key.Split('.');
                return new VsphereCounter
                {
                    Id = index + 1,
                    Group = parts[0],
                    Name = parts[1],
                    Rollup = VsphereCounter.ParseRollup(parts[2]),
                    Unit = parts[0] == "cpu" ? "percent" : "millisecond",
                    Level = key.StartsWith("disk.", StringComparison.Ordinal) ? 2 : 1,
                };
            });
    }

    private static VsphereObservationSource Source(
        FakeApi api, IVsphereSampleTargetProvider targets, IClock? clock = null) =>
        new(api, targets, clock ?? new FixedClock());

    /// <summary>A vCenter that admits to nothing, whatever it actually holds.</summary>
    private static FakeApi SilentProbe() => new() { Available = [] };

    [Fact]
    public async Task Samples_are_attributed_to_the_entity_they_belong_to()
    {
        var api = new FakeApi();
        var batch = await Source(api, new Targets("host-1", "host-2"))
            .ReadAsync(CancellationToken.None);

        Assert.Equal("vc-1", batch.SourceInstanceId);
        Assert.Contains(batch.Observations, o => o.Entity == new EntityId("host-1"));
        Assert.Contains(batch.Observations, o => o.Entity == new EntityId("host-2"));
        Assert.Empty(batch.Failures);
    }

    [Fact]
    public async Task A_sample_that_cannot_be_attributed_is_dropped_rather_than_guessed_at()
    {
        // An unattributed number is worse than no number: it looks like
        // knowledge.
        var targets = new Targets("host-1", "host-2");
        targets.Resolvable.Remove("host-2");

        var batch = await Source(new FakeApi(), targets).ReadAsync(CancellationToken.None);

        Assert.Contains(batch.Observations, o => o.Entity == new EntityId("host-1"));
        Assert.DoesNotContain(batch.Observations, o => o.Entity == new EntityId("host-2"));
    }

    [Fact]
    public async Task A_counter_the_platform_is_not_collecting_is_reported_not_silently_missing()
    {
        // The vSphere statistics level case. At level 1 the disk latency triad
        // is absent, and those three are what let the product say which layer
        // is slow rather than merely that something is.
        var api = new FakeApi
        {
            Available = [.. VsphereCounters.Host.Where(k => !k.StartsWith("disk.", StringComparison.Ordinal))],
        };

        var batch = await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        var insufficient = batch.Failures
            .Where(f => f.Kind == CollectionFailureKind.InsufficientDetailLevel)
            .ToList();

        Assert.Equal(4, insufficient.Count);
        Assert.Contains(insufficient, f => f.Target == "disk.deviceLatency.average");
        Assert.Contains(insufficient, f => f.Target == "disk.queueLatency.average");
        Assert.Contains(insufficient, f => f.Target == "disk.kernelLatency.average");
    }

    [Fact]
    public async Task The_counters_that_are_available_are_still_collected()
    {
        // Partial success: losing the disk triad must not cost the CPU and
        // memory readings too.
        var api = new FakeApi
        {
            Available = [.. VsphereCounters.Host.Where(k => !k.StartsWith("disk.", StringComparison.Ordinal))],
        };

        var batch = await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        Assert.Contains(batch.Observations, o => o.Value.CounterName == "cpu.usage.average");
        Assert.DoesNotContain(batch.Observations, o => o.Value.CounterName.StartsWith("disk.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_counter_this_vcenter_does_not_define_is_a_protocol_difference_not_a_config_problem()
    {
        var api = new FakeApi
        {
            Catalog = [.. new FakeApi().Catalog.Where(c => c.Key != "mem.swapused.average")],
        };

        var batch = await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        var failure = Assert.Single(batch.Failures, f => f.Target == "mem.swapused.average");
        Assert.Equal(CollectionFailureKind.ProtocolError, failure.Kind);
    }

    [Fact]
    public async Task The_batch_size_respects_the_servers_limit()
    {
        // 256 * 0.8 / 8 counters = 25 entities per query.
        var api = new FakeApi();
        var hosts = Enumerable.Range(0, 60).Select(i => $"host-{i}").ToArray();

        await Source(api, new Targets(hosts)).ReadAsync(CancellationToken.None);

        Assert.All(api.ObservedBatchSizes, size => Assert.True(size <= 25));
        Assert.Equal(60, api.ObservedBatchSizes.Sum());
    }

    [Fact]
    public async Task A_refused_query_is_retried_smaller_rather_than_abandoned()
    {
        // The server told us what it will accept; believing it is cheaper than
        // guessing, and a fixed guess is how the previous product's charts came
        // back empty.
        var api = new FakeApi { RefuseBatchesLargerThan = 5 };
        var hosts = Enumerable.Range(0, 20).Select(i => $"host-{i}").ToArray();

        var batch = await Source(api, new Targets(hosts)).ReadAsync(CancellationToken.None);

        Assert.Equal(20, batch.Observations.Select(o => o.Entity).Distinct().Count());
        Assert.Contains(api.ObservedBatchSizes, size => size > 5);  // the refused attempt
        Assert.Contains(api.ObservedBatchSizes, size => size <= 5); // the accepted one
    }

    [Fact]
    public async Task Having_had_to_shrink_the_query_is_reported()
    {
        // Silently succeeding more slowly hides a tuning problem an operator
        // could fix.
        var api = new FakeApi { RefuseBatchesLargerThan = 5 };
        var hosts = Enumerable.Range(0, 20).Select(i => $"host-{i}").ToArray();

        var batch = await Source(api, new Targets(hosts)).ReadAsync(CancellationToken.None);

        Assert.Contains(batch.Failures, f => f.Detail.Contains("reduced to", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_query_refused_even_for_one_entity_stops_rather_than_looping()
    {
        var api = new FakeApi { RefuseBatchesLargerThan = 0 };

        var batch = await Source(api, new Targets("host-1", "host-2")).ReadAsync(CancellationToken.None);

        Assert.Empty(batch.Observations);
        Assert.Contains(batch.Failures, f =>
            f.Detail.Contains("even for a single entity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_server_limit_falls_back_rather_than_failing()
    {
        // Null is a legitimate answer: the setting may be absent on older
        // versions or the account may not be allowed to read it.
        var api = new FakeApi { MaxQueryMetrics = null };

        var batch = await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        Assert.NotEmpty(batch.Observations);
        Assert.Empty(batch.Failures);
    }

    [Fact]
    public async Task Nothing_to_sample_is_not_an_error()
    {
        var batch = await Source(new FakeApi(), new Targets()).ReadAsync(CancellationToken.None);

        Assert.Empty(batch.Observations);
        Assert.Empty(batch.Failures);
    }

    [Fact]
    public async Task A_source_that_admits_to_nothing_is_still_asked_once()
    {
        // The first cycle queries regardless. Trusting the probe from the
        // outset is what made forty-one datastores read as unmeasured, and a
        // product that never asks cannot discover it was wrong to stop.
        var api = SilentProbe();

        await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        Assert.Single(api.ObservedQueries);
    }

    [Fact]
    public async Task And_then_left_alone_until_the_hour_is_up()
    {
        // Asking every thirty seconds for an answer that will not change is a
        // monitoring tool making work for the system it monitors.
        var api = new FakeApi
        {
            Available = [],
            EmptySamples = true,
        };
        var clock = new FixedClock();
        var source = Source(api, new Targets("host-1"), clock);

        await source.ReadAsync(CancellationToken.None);
        clock.UtcNow = T0.AddMinutes(30);
        await source.ReadAsync(CancellationToken.None);

        Assert.Single(api.ObservedQueries);
    }

    [Fact]
    public async Task And_asked_again_once_it_is()
    {
        // The skip expires. A statistics level raised this morning starts
        // producing data the same day rather than at the next restart.
        var api = new FakeApi
        {
            Available = [],
            EmptySamples = true,
        };
        var clock = new FixedClock();
        var source = Source(api, new Targets("host-1"), clock);

        await source.ReadAsync(CancellationToken.None);
        clock.UtcNow = T0.AddHours(2);
        await source.ReadAsync(CancellationToken.None);

        Assert.Equal(2, api.ObservedQueries.Count);
    }

    [Fact]
    public async Task A_probe_the_data_contradicts_stops_being_consulted()
    {
        // The point of re-checking. If the query returns data for a type the
        // probe dismissed, the probe is wrong here — and measuring once an
        // hour while the data was there all along would be its own defect.
        var api = SilentProbe();
        var clock = new FixedClock();
        var source = Source(api, new Targets("host-1"), clock);

        await source.ReadAsync(CancellationToken.None);
        clock.UtcNow = T0.AddSeconds(30);
        await source.ReadAsync(CancellationToken.None);
        clock.UtcNow = T0.AddSeconds(60);
        var third = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(3, api.ObservedQueries.Count);
        Assert.NotEmpty(third.Observations);
        Assert.Empty(third.Failures);
    }

    [Fact]
    public async Task A_source_that_admits_to_nothing_says_so_in_one_sentence()
    {
        // Not one complaint per counter. Against a live vCenter that produced
        // two messages each claiming a counter "requires statistics level 1",
        // which is not a thing that can be true — level 1 is the floor.
        var api = new FakeApi
        {
            Available = [],
            EmptySamples = true,
        };

        var batch = await Source(api, new Targets("host-1")).ReadAsync(CancellationToken.None);

        var failure = Assert.Single(batch.Failures);

        Assert.Equal(CollectionFailureKind.NotConfigured, failure.Kind);
        Assert.Equal("HostSystem", failure.Target);
        Assert.DoesNotContain("level 1", failure.Detail, StringComparison.OrdinalIgnoreCase);
    }
}