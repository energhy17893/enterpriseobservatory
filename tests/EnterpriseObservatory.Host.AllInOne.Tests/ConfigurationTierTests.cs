using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The configuration tier's own run: its role in collector_health, its budget,
/// and its first pass at start.
/// </summary>
public class ConfigurationTierTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 20, 38, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_read_over_budget_stays_partial_under_its_own_role()
    {
        var health = new InMemoryCollectorHealthStore();
        var pipeline = new ConfigurationCollectionPipeline(new TestClock(T0), health);
        var source = new Source("vc-1")
        {
            Read = new ConfigurationRead
            {
                ObjectsRead = 700,
                Complete = false,
                Failures =
                [
                    new CollectionFailure
                    {
                        Kind = CollectionFailureKind.Timeout,
                        Target = "configuration",
                        Detail = "cut off by its budget after 700 objects",
                    },
                ],
            },
        };

        var result = await pipeline.RunAsync([source], CollectionPolicy.Default, CancellationToken.None);

        var row = Assert.Single(health.Current);
        Assert.Equal(CollectorRole.Configuration, row.Role);
        Assert.Equal(HealthState.Warning, row.Health);
        Assert.False(row.Up);
        Assert.Equal(700, row.ItemsRead);
        Assert.Equal(CollectionFailureKind.Timeout, Assert.Single(row.PartialFailures).Kind);
        Assert.False(Assert.Single(result.Reads).Read.Complete);
    }

    [Fact]
    public async Task A_pass_skipped_for_a_fresh_carry_is_not_a_failure()
    {
        var health = new InMemoryCollectorHealthStore();

        var result = await new ConfigurationCollectionPipeline(new TestClock(T0), health)
            .RunAsync(
                [new Source("vc-1") { Read = new ConfigurationRead { Skipped = true } }],
                CollectionPolicy.Default,
                CancellationToken.None);

        var row = Assert.Single(health.Current);
        Assert.True(row.Up);
        Assert.Equal(HealthState.Healthy, row.Health);
        Assert.Null(row.ItemsRead);
        Assert.Empty(row.PartialFailures);
        Assert.True(Assert.Single(result.Reads).Read.Skipped);
    }

    [Fact]
    public async Task Its_health_row_does_not_touch_the_inventory_row()
    {
        var health = new InMemoryCollectorHealthStore();
        health.Merge([new CollectorHealth { InstanceId = "vc-1", Role = CollectorRole.Inventory, Health = HealthState.Healthy }]);

        await new ConfigurationCollectionPipeline(new TestClock(T0), health)
            .RunAsync([new Source("vc-1")], CollectionPolicy.Default, CancellationToken.None);

        Assert.Equal(HealthState.Healthy, health.Current.Single(h => h.Role == CollectorRole.Inventory).Health);
        Assert.True(health.Current.Single(h => h.Role == CollectorRole.Configuration).Up);
    }

    [Fact]
    public async Task The_first_configuration_pass_runs_at_start_once_the_first_inventory_read_is_done()
    {
        // After it, not beside it: that fast read seeds the carry, and a pass
        // racing it would read the ~41 MB a second time.
        var clock = new TestClock(T0);
        var health = new InMemoryCollectorHealthStore();
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var source = new Source("vc-1") { Order = order };
        var graphs = new InMemoryEntityGraphStore();
        var alerts = new InMemoryAlertStateStore();
        var observations = new InMemoryObservationStore();
        var events = new InMemoryEventStore();

        // Every interval an hour: anything that ran did so at start.
        var options = new MonitoringOptions
        {
            InventoryInterval = TimeSpan.FromHours(1),
            ObservationInterval = TimeSpan.FromHours(1),
            ConfigurationInterval = TimeSpan.FromHours(1),
        };

        var cycle = new MonitoringCycle(
            new InventoryCollectionPipeline(clock),
            new ObservationCollectionPipeline(clock),
            graphs,
            alerts,
            health,
            new InMemoryCoverageStore(),
            new RecordingNotifier(),
            observations,
            new InMemoryMaintenanceWindowStore(),
            clock,
            events);

        using var worker = new MonitoringWorker(
            cycle,
            new Registry(source, new Inventory("vc-1", order)),
            options,
            new EventCollectionPipeline(events, clock),
            new ComplianceService(
                [ContinuityCatalogue.Build(ContinuityCatalogue.Production)],
                new InMemoryComplianceStore(),
                clock,
                ContinuityCatalogue.ChecksById(ContinuityCatalogue.Production)),
            graphs,
            alerts,
            observations,
            new OperationalMetricsStore(),
            new TestConnectionStore(),
            new ConfigurationCollectionPipeline(clock, health),
            NullLogger<MonitoringWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!health.Current.Any(h => h.Role == CollectorRole.Configuration) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, source.Reads);
        Assert.Equal(["inventory", "configuration"], order.ToArray());
        Assert.Contains(health.Current, h => h.Role == CollectorRole.Configuration && h.InstanceId == "vc-1");
    }

    private sealed class Source(string instanceId) : IConfigurationTierSource
    {
        private int _reads;

        public string InstanceId { get; } = instanceId;

        public ConfigurationRead Read { get; init; } = new() { ObjectsRead = 1159 };

        public int Reads => Volatile.Read(ref _reads);

        public System.Collections.Concurrent.ConcurrentQueue<string>? Order { get; init; }

        public Task<ConfigurationRead> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            Order?.Enqueue("configuration");
            return Task.FromResult(Read);
        }
    }

    private sealed class Inventory(string instanceId, System.Collections.Concurrent.ConcurrentQueue<string> order)
        : IInventorySource
    {
        public string InstanceId { get; } = instanceId;

        public async Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            // Slow enough that a pass not waiting for it would come first.
            await Task.Delay(200, cancellationToken);
            order.Enqueue("inventory");
            return new InventorySnapshot { SourceInstanceId = InstanceId, ReadAtUtc = T0 };
        }
    }

    private sealed class Registry(IConfigurationTierSource configuration, IInventorySource? inventory = null)
        : ISourceRegistry
    {
        public IReadOnlyList<IInventorySource> Inventory => inventory is null ? [] : [inventory];

        public IReadOnlyList<IObservationSource> Observations => [];

        public IReadOnlyList<IEventSource> Events => [];

        public IReadOnlyList<IConfigurationTierSource> Configuration => [configuration];
    }
}
