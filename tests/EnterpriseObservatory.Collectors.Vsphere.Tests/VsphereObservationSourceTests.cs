using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

public class VsphereObservationSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
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

        /// <summary>Batch sizes the server refuses, to exercise adaptive sizing.</summary>
        public int RefuseBatchesLargerThan { get; init; } = int.MaxValue;

        public List<int> ObservedBatchSizes { get; } = [];

        public Task<IReadOnlyList<VsphereCounter>> GetCounterCatalogAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VsphereCounter>>(Catalog);

        public Task<int?> GetMaxQueryMetricsAsync(CancellationToken ct) =>
            Task.FromResult(MaxQueryMetrics);

        public Task<IReadOnlyList<string>> GetAvailableCounterKeysAsync(
            string entityMoRef, VsphereEntityType entityType, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                [.. Available ?? [.. Catalog.Select(c => c.Key)]]);

        public Task<IReadOnlyList<PerfEntitySamples>> QueryPerfAsync(
            IReadOnlyList<string> entityMoRefs,
            VsphereEntityType entityType,
            IReadOnlyList<VsphereCounter> counters,
            CancellationToken ct)
        {
            ObservedBatchSizes.Add(entityMoRefs.Count);

            if (entityMoRefs.Count > RefuseBatchesLargerThan)
            {
                throw new VsphereQuerySizeRefusedException("Request processing is restricted by administrator.");
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

    private static VsphereObservationSource Source(FakeApi api, IVsphereSampleTargetProvider targets) =>
        new(api, targets, new FixedClock());

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
}
