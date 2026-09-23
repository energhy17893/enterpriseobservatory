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

    /// <summary>
    /// F6, ADR-0025 §5: §10.2's minimum list -- answered, duration, items
    /// read/unread, skipped cycles, total attempts -- exists after a healthy
    /// read and after a failed one, produced entirely by the runner. Held
    /// sessions and clock skew are asserted separately below
    /// (<see cref="Self_metrics_are_produced_by_the_runner_even_when_the_collector_emits_none"/>):
    /// they are null here whenever the fixture's fake does not offer
    /// <c>IVsphereChannelSelfMetrics</c>, and null is exactly what "the
    /// collector emits none" must read as, not a failure of this case.
    /// </summary>
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
        Assert.True(okHealth.Up);
        Assert.NotNull(okHealth.LastDuration);
        Assert.NotEmpty(okHealth.RecentDurations);
        Assert.NotNull(okHealth.ItemsRead);
        Assert.NotNull(okHealth.ItemsUnread);
        Assert.Equal(1, okHealth.TotalAttempts);
        Assert.Equal(0, okHealth.TotalFailures);

        var slowHealth = Assert.Single(slow.Health);
        Assert.NotNull(slowHealth.LastAttemptUtc);
        Assert.False(slowHealth.Up);
        Assert.NotNull(slowHealth.LastDuration);
        Assert.NotEmpty(slowHealth.RecentDurations);
        // Not reached, so nothing was read -- null, not zero (§10.6's rule).
        Assert.Null(slowHealth.ItemsRead);
        Assert.Null(slowHealth.ItemsUnread);
        Assert.Equal(1, slowHealth.TotalAttempts);
        Assert.Equal(1, slowHealth.TotalFailures);
    }

    /// <summary>
    /// F6's own new case: the runner's self-metrics exist even for a source
    /// that offers none of its own -- no <see cref="IObservationSource.SessionsHeld"/>,
    /// no <see cref="IObservationSource.GetServerTimeAsync"/> -- proving these
    /// numbers are the runner's to produce, not a courtesy a collector has to
    /// remember to supply. <typeparamref name="TFixture"/>'s own healthy
    /// source already is this case today (its fakes do not implement
    /// <c>IVsphereChannelSelfMetrics</c>); a source with literally the
    /// interface's bare defaults proves it independent of any one collector.
    /// </summary>
    [Fact]
    public async Task Self_metrics_are_produced_by_the_runner_even_when_the_collector_emits_none()
    {
        var pipeline = new ObservationCollectionPipeline(new TestClock());

        var result = await pipeline.RunAsync(
            [new BareSource()], [], CollectionPolicy.Default, CancellationToken.None);

        var health = Assert.Single(result.Health);
        Assert.True(health.Up);
        Assert.NotNull(health.LastAttemptUtc);
        Assert.NotNull(health.LastDuration);
        Assert.Equal(1, health.TotalAttempts);

        // Exactly what "emits none" means: not invented as zero.
        Assert.Null(health.SessionsHeld);
        Assert.Null(health.ClockSkewSeconds);
    }

    /// <summary>An observation source offering nothing beyond what <see cref="IObservationSource"/> requires.</summary>
    private sealed class BareSource : IObservationSource
    {
        public string InstanceId => "bare";

        public Task<ObservationBatch> ReadAsync(ObservationReadContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ObservationBatch { SourceInstanceId = InstanceId, ReadAtUtc = DateTimeOffset.UtcNow });
    }

    // --- case: up means Prometheus's up, not the Health rollup -------------

    /// <summary>
    /// A live estate reads Observation as permanently Warning because Storage
    /// I/O Control is off everywhere, so <c>datastore.datastoreVMObservedLatency.latest</c>
    /// is <see cref="CollectionFailureKind.NotConfigured"/> on every cycle.
    /// That is a fact about the estate, not a failed scrape: <c>up</c> must
    /// stay true, with the shortfall visible through <c>ItemsUnread</c>/
    /// <c>PartialFailures</c> instead — the whole reason <c>up</c> is not
    /// simply <c>Health == Healthy</c>.
    /// </summary>
    [Fact]
    public async Task A_read_with_only_a_configuration_partial_failure_is_up_with_items_unread()
    {
        var pipeline = new ObservationCollectionPipeline(new TestClock());

        var result = await pipeline.RunAsync(
            [new PartiallyConfiguredSource()], [], CollectionPolicy.Default, CancellationToken.None);

        var health = Assert.Single(result.Health);
        Assert.True(health.Up);
        Assert.Equal(HealthState.Warning, health.Health);
        Assert.True(health.ItemsUnread > 0);
        Assert.Contains(health.PartialFailures, f => f.Kind == CollectionFailureKind.NotConfigured);
    }

    /// <summary>A source estate-wide not configured for one counter, otherwise clean.</summary>
    private sealed class PartiallyConfiguredSource : IObservationSource
    {
        public string InstanceId => "partially-configured";

        public Task<ObservationBatch> ReadAsync(ObservationReadContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ObservationBatch
            {
                SourceInstanceId = InstanceId,
                ReadAtUtc = DateTimeOffset.UtcNow,
                Observations =
                [
                    new Observation
                    {
                        Entity = EntityId.For(InstanceId, "host-1"),
                        Value = new CounterValue
                        {
                            CounterName = "cpu.usage.average",
                            Raw = 10,
                            Rollup = RollupType.Average,
                            Interval = TimeSpan.FromSeconds(20),
                            Unit = "percent",
                        },
                        SampledAtUtc = DateTimeOffset.UtcNow,
                        Source = InstanceId,
                    },
                ],
                Failures =
                [
                    new CollectionFailure
                    {
                        Kind = CollectionFailureKind.NotConfigured,
                        Target = "datastore.datastoreVMObservedLatency.latest",
                        Detail = "Storage I/O Control is not active on this datastore.",
                    },
                ],
            });
    }

    /// <summary>A source whose one entity type fails to parse — this fixture's stand-in for a bad reply.</summary>
    /// <remarks>
    /// <see cref="IObservationContractFixture.CreateWithOneFailingEntityType"/>
    /// already is this case: the vSphere fixture reports it with
    /// <see cref="CollectionFailureKind.ProtocolError"/> (a vCenter fault that
    /// is neither a rejected credential nor a permission problem, the same
    /// bucket a genuinely malformed reply falls into), which is not an
    /// environment condition and must not read as up.
    /// </remarks>
    [Fact]
    public async Task A_parse_failure_is_not_up()
    {
        var fixture = new TFixture();
        var pipeline = new ObservationCollectionPipeline(new TestClock());

        var result = await pipeline.RunAsync(
            [fixture.CreateWithOneFailingEntityType()], [], CollectionPolicy.Default, CancellationToken.None);

        var health = Assert.Single(result.Health);
        Assert.False(health.Up);
    }

    /// <summary>
    /// The read itself succeeds, but bookkeeping it depended on — here, the
    /// collection-gap record — refuses to write. §10.6's "the store refuses
    /// the write" case: reached and parsed is not enough for <c>up</c> if
    /// what the read produced could not be kept.
    /// </summary>
    [Fact]
    public async Task A_write_the_read_depended_on_being_refused_is_not_up()
    {
        var fixture = new TFixture();
        var pipeline = new ObservationCollectionPipeline(new TestClock(), gaps: new ThrowingGapStore());

        var result = await pipeline.RunAsync(
            [fixture.CreateHealthy(3)], [], CollectionPolicy.Default, CancellationToken.None);

        var health = Assert.Single(result.Health);
        Assert.False(health.Up);
    }

    /// <summary>A gap store that refuses every call, for the "store refuses the write" case.</summary>
    private sealed class ThrowingGapStore : ICollectionGapStore
    {
        public IReadOnlyDictionary<EntityId, DateTimeOffset> LatestSampleTimes(IReadOnlyCollection<EntityId> entities) =>
            throw new InvalidOperationException("The gap store is unreachable.");

        public IReadOnlyList<CollectionGap> OpenGaps(string sourceInstanceId) =>
            throw new InvalidOperationException("The gap store is unreachable.");

        public IReadOnlyList<CollectionGap> Gaps(string sourceInstanceId) => [];

        public CollectionGap Open(CollectionGap gap) => throw new InvalidOperationException("The gap store is unreachable.");

        public void Update(CollectionGap gap) => throw new InvalidOperationException("The gap store is unreachable.");

        public IReadOnlyDictionary<CollectionGapState, int> CountsByState() =>
            new Dictionary<CollectionGapState, int>();
    }

    /// <summary>The abandoned-read case from <see cref="Self_metrics_are_present_after_every_result_healthy_or_not"/>, named on its own.</summary>
    [Fact]
    public async Task An_abandoned_read_is_not_up()
    {
        var fixture = new TFixture();
        var pipeline = new ObservationCollectionPipeline(new TestClock());

        var result = await pipeline.RunAsync(
            [fixture.CreateSlow()], [], FastTimeoutPolicy, CancellationToken.None);

        var health = Assert.Single(result.Health);
        Assert.False(health.Up);
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

            // F5b: the inventory's capacity readings wait in the same queue,
            // as current state — counted the same way, never a gap.
            var capacity = Capacity(fixture.InstanceId, clock.UtcNow);
            produced.AddRange(capacity);
            queue.EnqueueCurrentState(fixture.InstanceId, capacity);

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
        Assert.Equal(smallBudget, after.DroppedOverBudgetRows + after.DroppedTooOldRows > 0);

        // While the store was down each newer capacity reading replaced the
        // one still waiting: dropped as current state, whatever the budget.
        Assert.True(after.DroppedCurrentStateRows > 0);

        // Accepted exactly once: what the store holds is what was counted
        // accepted, each produced row at most once, nothing it never produced.
        Assert.Equal(after.AcceptedRows, store.Kept.Count);
        Assert.Equal(store.Kept.Count, store.Kept.Distinct(ReferenceEqualityComparer.Instance).Count());
        var producedSet = produced.ToHashSet(ReferenceEqualityComparer.Instance);
        Assert.All(store.Kept, row => Assert.Contains(row, producedSet));
    }

    /// <summary>Two datastores' used-space readings, as an inventory read carries them.</summary>
    private static IReadOnlyList<Observation> Capacity(string source, DateTimeOffset at) =>
    [
        .. Enumerable.Range(0, 2).Select(i => CapacityCounters.Reading(
            EntityId.For(source, $"datastore-{i}"), CapacityCounters.DatastoreUsed, 1e9 * (i + 1), at, source)),
    ];

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
