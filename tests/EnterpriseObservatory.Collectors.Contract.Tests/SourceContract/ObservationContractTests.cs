using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// The source-level half of the observation collector contract suite
/// (ADR-0025, roadmap T2.1): what <see cref="ObservationCollectionPipeline"/>
/// guarantees for any <c>IObservationSource</c> plugged into it, proven here
/// against today's vSphere observation source.
/// </summary>
public abstract class ObservationContractTests<TFixture>
    where TFixture : IObservationContractFixture, new()
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
        var pipeline = new ObservationCollectionPipeline(new TestClock());

        var result = await pipeline.RunAsync(
            [fixture.CreateSlow()], [], FastTimeoutPolicy, CancellationToken.None);

        var health = Assert.Single(result.Health);
        Assert.Equal(fixture.InstanceId, health.InstanceId);
        Assert.Equal(HealthState.Unknown, health.Health);
        Assert.Equal(1, health.ConsecutiveFailures);
        Assert.Empty(result.Batches);
        Assert.NotEmpty(result.CollectionAlerts);
    }

    // --- case: self-metrics are present after every result -----------------

    [Fact]
    public async Task Self_metrics_are_present_after_every_result_healthy_or_not()
    {
        var fixture = new TFixture();
        var pipeline = new ObservationCollectionPipeline(new TestClock());

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
        var pipeline = new ObservationCollectionPipeline(new TestClock());
        var source = fixture.CreateHealthy(10);

        IReadOnlyList<CollectorHealth> health = [];
        var observationCounts = new List<int>();

        for (var cycle = 0; cycle < 50; cycle++)
        {
            var result = await pipeline.RunAsync(
                [source], health, CollectionPolicy.Default, CancellationToken.None);

            health = result.Health;
            observationCounts.Add(result.Observations.Count);
        }

        Assert.All(observationCounts, c => Assert.Equal(observationCounts[0], c));
        Assert.All(health, h => Assert.Equal(0, h.ConsecutiveFailures));
        Assert.All(health, h => Assert.Empty(h.PartialFailures));
    }

    // --- balance rule: produced = accepted + dropped, nothing vanishes -----

    [Fact]
    public async Task Every_entity_type_read_is_accepted_or_dropped_never_both_never_neither()
    {
        var fixture = new TFixture();
        var source = fixture.CreateWithOneFailingEntityType();

        var batch = await source.ReadAsync(CancellationToken.None);

        // Two entity types were asked for (see the fixture): the host type
        // produced samples, the virtual-machine type was reported as a
        // failure. Neither is silent, and neither is both accepted and
        // dropped.
        Assert.NotEmpty(batch.Observations);
        Assert.All(batch.Observations, o => Assert.StartsWith("host-", o.Entity.Value, StringComparison.Ordinal));

        // The target names the entity type; its exact wording (e.g. the fault
        // kind suffix package A adds) is not part of the contract.
        var failure = Assert.Single(batch.Failures);
        Assert.StartsWith("VirtualMachine", failure.Target, StringComparison.Ordinal);
    }
}
