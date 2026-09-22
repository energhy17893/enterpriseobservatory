using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// The source-level half of the inventory collector contract suite
/// (ADR-0025, roadmap T2.1): what <see cref="InventoryCollectionPipeline"/>
/// guarantees for any <c>IInventorySource</c> plugged into it, proven here
/// against today's vSphere source.
/// </summary>
public abstract class InventoryContractTests<TFixture>
    where TFixture : IInventoryContractFixture, new()
{
    private static readonly CollectionPolicy FastTimeoutPolicy = new()
    {
        SourceTimeout = TimeSpan.FromMilliseconds(200),
        MaxRetries = 0,
        MaxConcurrency = 4,
    };

    // --- case: a slow source is skipped (abandoned) and counted -----------

    [Fact]
    public async Task A_slow_source_is_abandoned_this_cycle_and_counted_as_a_failure()
    {
        var fixture = new TFixture();
        var pipeline = new InventoryCollectionPipeline(new TestClock());

        var result = await pipeline.RunAsync(
            [fixture.CreateSlow()], [], FastTimeoutPolicy, CancellationToken.None);

        var health = Assert.Single(result.Health);
        Assert.Equal(fixture.InstanceId, health.InstanceId);
        Assert.Equal(HealthState.Unknown, health.Health);
        Assert.Equal(1, health.ConsecutiveFailures);

        // Not blocked forever, and not silently ignored: this cycle produced
        // no snapshot for it, but it was named.
        Assert.Empty(result.Snapshots);
        Assert.NotEmpty(result.CollectionAlerts);
    }

    // --- case: self-metrics are present after every result -----------------

    [Fact]
    public async Task Self_metrics_are_present_after_every_result_healthy_or_not()
    {
        var fixture = new TFixture();
        var pipeline = new InventoryCollectionPipeline(new TestClock());

        var healthy = await pipeline.RunAsync(
            [fixture.CreateHealthy(3)], [], CollectionPolicy.Default, CancellationToken.None);
        var slow = await pipeline.RunAsync(
            [fixture.CreateSlow()], [], FastTimeoutPolicy, CancellationToken.None);

        var okHealth = Assert.Single(healthy.Health);
        Assert.NotNull(okHealth.LastAttemptUtc);

        var slowHealth = Assert.Single(slow.Health);
        Assert.NotNull(slowHealth.LastAttemptUtc);
    }

    // --- case: state and memory stay constant over a long run --------------

    [Fact]
    public async Task Output_size_and_health_state_stay_constant_over_a_long_run()
    {
        var fixture = new TFixture();
        var pipeline = new InventoryCollectionPipeline(new TestClock());
        var source = fixture.CreateHealthy(25);

        IReadOnlyList<CollectorHealth> health = [];
        var entityCounts = new List<int>();

        for (var cycle = 0; cycle < 50; cycle++)
        {
            var result = await pipeline.RunAsync(
                [source], health, CollectionPolicy.Default, CancellationToken.None);

            health = result.Health;
            entityCounts.Add(result.Snapshots.Single().Entities.Count);
        }

        // A source that keeps answering the same estate must keep reporting
        // the same size, cycle after cycle — growth here would be a leak in
        // the source's own state, not in the runner.
        Assert.All(entityCounts, c => Assert.Equal(entityCounts[0], c));
        Assert.All(health, h => Assert.Equal(0, h.ConsecutiveFailures));
        Assert.All(health, h => Assert.Empty(h.PartialFailures));
    }

    // --- balance rule: produced = accepted + dropped, nothing vanishes -----

    [Fact]
    public async Task Every_read_target_is_accepted_or_dropped_never_both_never_neither()
    {
        var fixture = new TFixture();
        var source = fixture.CreateWithFailures(hostCount: 12, failureCount: 5);

        var snapshot = await source.ReadAsync(CancellationToken.None);

        var accepted = snapshot.Entities.Where(e => e.Kind == EntityKind.EsxiHost).ToList();
        var dropped = snapshot.Failures;

        Assert.Equal(12, accepted.Count);
        Assert.Equal(5, dropped.Count);

        // No duplicate accepted host and no duplicate dropped target — the
        // other half of "nothing vanishes" is "nothing is counted twice".
        Assert.Equal(accepted.Count, accepted.Select(e => e.Id).Distinct().Count());
        Assert.Equal(dropped.Count, dropped.Select(f => f.Target).Distinct().Count());
    }
}
