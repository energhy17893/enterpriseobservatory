using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Monitoring;
using Microsoft.Extensions.Logging;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// HostLog 1064: one line per source per metric cycle, so the host log keeps
/// the cadence a later cycle is compared against.
/// </summary>
public class ObservationSourceLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Each_metric_cycle_logs_one_line_per_polled_source_with_types_read_of_total_and_out_of_time()
    {
        var clock = new TestClock(T0);
        var health = new InMemoryCollectorHealthStore();
        var graphs = new InMemoryEntityGraphStore();
        var alerts = new InMemoryAlertStateStore();
        var observations = new InMemoryObservationStore();
        var events = new InMemoryEventStore();
        var logs = new CapturingLoggerProvider();

        var vcenter = new FakeObservationSource("vc-1")
        {
            Behaviour = () => new ObservationBatch
            {
                SourceInstanceId = "vc-1",
                ReadAtUtc = T0,
                Coverage =
                [
                    new PropertyCoverage { ObjectType = "HostSystem", Property = "realtime", Asked = 26, Answered = 26 },
                    new PropertyCoverage { ObjectType = "VirtualMachine", Property = "realtime", Asked = 699, Answered = 410 },
                ],
                Failures =
                [
                    new CollectionFailure
                    {
                        Kind = CollectionFailureKind.Timeout,
                        Target = "VirtualMachine",
                        Detail = "The read budget ran out with 410 of 699 VirtualMachine entities read",
                    },
                ],
            },
        };

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

        using var factory = new LoggerFactory([logs]);
        using var worker = new MonitoringWorker(
            cycle,
            new Registry([vcenter, new FakeRoleNotApplicableSource("svt-1")]),
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
            factory.CreateLogger<MonitoringWorker>());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!Lines(logs).Any() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var line = Assert.Single(Lines(logs));
        Assert.StartsWith("Information ", line, StringComparison.Ordinal);
        Assert.Contains("Observation vc-1: read in ", line, StringComparison.Ordinal);
        Assert.Contains(" s budget; read HostSystem 26/26, VirtualMachine 410/699; ", line, StringComparison.Ordinal);
        Assert.Contains("out of time: The read budget ran out with 410 of 699", line, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Lines, l => l.Contains("Observation svt-1", StringComparison.Ordinal));
    }

    private static IEnumerable<string> Lines(CapturingLoggerProvider logs) =>
        logs.Lines.Where(l => l.Contains("Observation vc-1:", StringComparison.Ordinal));

    private sealed class Registry(IReadOnlyList<IObservationSource> observations) : ISourceRegistry
    {
        public IReadOnlyList<IInventorySource> Inventory => [];

        public IReadOnlyList<IObservationSource> Observations => observations;

        public IReadOnlyList<IEventSource> Events => [];

        public IReadOnlyList<IConfigurationTierSource> Configuration => [];
    }
}
