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

        var batch = await new ObservationSourceSlot(fixture.InstanceId).ReadAsync(source, CancellationToken.None);

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

    // --- case: a value returned before it is filled is written exactly once --

    /// <summary>
    /// Same family as the T0.4 double-count fix: every sample is written
    /// once — not zero times because the platform said "later", not twice
    /// because it was asked again — and a figure combined across devices is
    /// never written from some of them.
    /// </summary>
    [Fact]
    public async Task A_sample_returned_with_placeholders_is_read_again_and_each_value_written_exactly_once()
    {
        var scenario = new TFixture().CreateWithLateValues();

        var batches = new List<ObservationBatch>();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var batch = await scenario.ReadCycleAsync(cycle);
            scenario.Accept(batch);
            batches.Add(batch);
        }

        var written = batches
            .SelectMany(b => b.Observations.Concat(b.Backfill))
            .Where(o => o.Value.CounterName != CollectorSelfMetrics.ClockSkewCounter)
            .Select(o => (Series: $"{o.Value.CounterName}|{o.Value.Instance}", At: o.SampledAtUtc, o.Value.Raw))
            .ToList();

        // Read again once filled: every series of the late sample is there.
        Assert.Equal(
            scenario.SeriesPerSample.Order(StringComparer.Ordinal),
            written.Where(w => w.At == scenario.LateSampleAt).Select(w => w.Series).Order(StringComparer.Ordinal));

        // Written exactly once, that sample and every other.
        Assert.All(written.GroupBy(w => (w.Series, w.At)), g => Assert.Single(g));

        // The combined figure is over every device, never over the ones that
        // happened to be in when first read.
        Assert.Equal(
            scenario.Combined.Value,
            Assert.Single(written, w => w.At == scenario.LateSampleAt && w.Series == scenario.Combined.Series).Raw);
    }

    // --- case: a store failure loses nothing: produced = accepted + dropped --

    /// <summary>
    /// §10.2's "depo hatasında kayıp ve çift yok", which stood skipped until
    /// the store queue existed (F5, ADR-0025 §6): the store fails mid-run,
    /// every row the source produced is afterwards either accepted exactly
    /// once or counted as dropped, never both and never neither.
    /// </summary>
    /// <remarks>
    /// Twice: with the default budget, which holds the whole outage, and with
    /// one that holds two and a half batches, so the outage forces drops.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_store_failure_does_not_lose_data_produced_equals_accepted_plus_dropped(bool smallBudget)
    {
        var fixture = new TFixture();
        var clock = new TestClock();
        var pipeline = new ObservationCollectionPipeline(clock);
        var source = fixture.CreateHealthy(4);
        var store = new FlakyStore();

        var probe = await new ObservationSourceSlot(fixture.InstanceId)
            .ReadAsync(fixture.CreateHealthy(4), CancellationToken.None);
        var rowsPerBatch = probe.Observations.Count + probe.Backfill.Count;

        var limits = smallBudget
            ? new StoreQueueLimits { BudgetBytes = StoreQueueLimits.MeasuredBytesPerRow * rowsPerBatch * 5L / 2 }
            : StoreQueueLimits.Default;
        var queue = new ObservationStoreQueue(store.Append, clock, limits);

        var produced = new List<Observation>();
        IReadOnlyList<CollectorHealth> health = [];

        for (var cycle = 0; cycle < 12; cycle++)
        {
            clock.UtcNow = clock.UtcNow.AddSeconds(30);

            // Down for a third of the run, in the middle: every write fails
            // before it lands, as a database that is not there does.
            store.Down = cycle is >= 4 and < 8;

            var result = await pipeline.RunAsync([source], health, CollectionPolicy.Default, CancellationToken.None);
            health = result.Health;

            foreach (var batch in result.Batches)
            {
                produced.AddRange(batch.Observations);
                produced.AddRange(batch.Backfill);
                queue.Enqueue(batch);
            }

            queue.Drain();
        }

        var after = queue.Snapshot();

        Assert.NotEmpty(produced);
        Assert.Equal(0, after.Rows);
        Assert.Equal(produced.Count, after.ProducedRows);
        Assert.Equal(after.ProducedRows, after.AcceptedRows + after.DroppedRows);
        Assert.Equal(smallBudget, after.DroppedRows > 0);

        // Accepted exactly once: what the store holds is what was counted
        // accepted, each produced row at most once, nothing it never produced.
        Assert.Equal(after.AcceptedRows, store.Kept.Count);
        Assert.Equal(store.Kept.Count, store.Kept.Distinct(ReferenceEqualityComparer.Instance).Count());
        var producedSet = produced.ToHashSet(ReferenceEqualityComparer.Instance);
        Assert.All(store.Kept, row => Assert.Contains(row, producedSet));
    }

    /// <summary>A store that refuses every write while it is down, and keeps nothing it refused.</summary>
    private sealed class FlakyStore
    {
        public bool Down { get; set; }

        public List<Observation> Kept { get; } = [];

        public void Append(IReadOnlyList<Observation> rows)
        {
            if (Down)
            {
                throw new InvalidOperationException("The database is not there.");
            }

            Kept.AddRange(rows);
        }
    }
}
