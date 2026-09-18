using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Tests;

public class ObservationCollectionPipelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeSource(string id) : IObservationSource
    {
        public string InstanceId { get; } = id;

        public int Attempts { get; private set; }

        public Func<int, Task<ObservationBatch>>? Behaviour { get; init; }

        public Task<ObservationBatch> ReadAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            return Behaviour is null ? Task.FromResult(Batch(InstanceId)) : Behaviour(Attempts);
        }
    }

    private static ObservationBatch Batch(string id, params CollectionFailure[] failures) => new()
    {
        SourceInstanceId = id,
        ReadAtUtc = T0,
        Observations =
        [
            new Observation
            {
                Entity = new EntityId("esx01"),
                Source = id,
                SampledAtUtc = T0,
                Value = new CounterValue
                {
                    CounterName = "cpu.usage.average",
                    Raw = 42,
                    Rollup = RollupType.Average,
                    Interval = TimeSpan.FromSeconds(20),
                    Unit = "percent",
                },
            },
        ],
        Failures = failures,
    };

    private static readonly CollectionPolicy Fast = CollectionPolicy.Default with
    {
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        SourceTimeout = TimeSpan.FromSeconds(2),
    };

    private static ObservationCollectionPipeline Pipeline() => new(new FixedClock(T0));

    [Fact]
    public async Task Samples_from_every_source_are_gathered()
    {
        var result = await Pipeline().RunAsync(
            [new FakeSource("vc-1"), new FakeSource("vc-2")], [], Fast, CancellationToken.None);

        Assert.Equal(2, result.Batches.Count);
        Assert.Equal(2, result.Observations.Count);
        Assert.All(result.Health, h => Assert.Equal(HealthState.Healthy, h.Health));
    }

    [Fact]
    public async Task One_source_failing_does_not_cost_the_others_their_samples()
    {
        var good = new FakeSource("vc-1");
        var bad = new FakeSource("vc-2")
        {
            Behaviour = _ => throw new InvalidOperationException("down"),
        };

        var result = await Pipeline().RunAsync([good, bad], [], Fast, CancellationToken.None);

        Assert.Single(result.Observations);
        Assert.Contains(result.Health, h => h.InstanceId == "vc-2" && h.Health == HealthState.Unknown);
    }

    [Fact]
    public async Task The_same_resilience_policy_applies_as_for_inventory()
    {
        // The two pipelines exist because they are different rhythms, not
        // because they should fail differently.
        var flaky = new FakeSource("vc-1")
        {
            Behaviour = attempt => attempt < 3
                ? throw new InvalidOperationException("temporary")
                : Task.FromResult(Batch("vc-1")),
        };

        var result = await Pipeline().RunAsync([flaky], [], Fast, CancellationToken.None);

        Assert.Equal(3, flaky.Attempts);
        Assert.Single(result.Batches);
    }

    [Fact]
    public async Task An_insufficient_detail_level_raises_a_configuration_alert()
    {
        // The vSphere statistics level case. At level 1 the disk latency triad
        // is not collected at all, and those three are what let the product say
        // which layer is slow rather than merely that something is.
        var limited = new FakeSource("vc-1")
        {
            Behaviour = _ => Task.FromResult(Batch("vc-1", new CollectionFailure
            {
                Kind = CollectionFailureKind.InsufficientDetailLevel,
                Target = "disk.deviceLatency.average",
                Detail = "Statistics level 1; level 2 required.",
            }, new CollectionFailure
            {
                Kind = CollectionFailureKind.InsufficientDetailLevel,
                Target = "disk.queueLatency.average",
                Detail = "Statistics level 1; level 2 required.",
            })),
        };

        var result = await Pipeline().RunAsync([limited], [], Fast, CancellationToken.None);

        var alert = Assert.Single(result.CollectionAlerts);
        Assert.Equal("Platform detail level too low", alert.Title);
        Assert.Contains("disk.deviceLatency.average", alert.Description, StringComparison.Ordinal);
        Assert.Contains("disk.queueLatency.average", alert.Description, StringComparison.Ordinal);
        Assert.Contains("Unknown", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_alert_per_source_rather_than_one_per_missing_counter()
    {
        // Ten missing counters on one vCenter is one configuration problem, not
        // ten. Fingerprinting on the source is what makes that true.
        var failures = Enumerable.Range(0, 10).Select(i => new CollectionFailure
        {
            Kind = CollectionFailureKind.InsufficientDetailLevel,
            Target = $"counter.{i}",
            Detail = "Statistics level too low.",
        }).ToArray();

        var limited = new FakeSource("vc-1")
        {
            Behaviour = _ => Task.FromResult(Batch("vc-1", failures)),
        };

        var result = await Pipeline().RunAsync([limited], [], Fast, CancellationToken.None);

        Assert.Single(result.CollectionAlerts);
    }

    [Fact]
    public async Task A_source_that_reads_everything_raises_no_configuration_alert()
    {
        var result = await Pipeline().RunAsync([new FakeSource("vc-1")], [], Fast, CancellationToken.None);

        Assert.Empty(result.CollectionAlerts);
    }

    [Fact]
    public async Task A_partially_readable_source_is_degraded_rather_than_healthy()
    {
        var limited = new FakeSource("vc-1")
        {
            Behaviour = _ => Task.FromResult(Batch("vc-1", new CollectionFailure
            {
                Kind = CollectionFailureKind.InsufficientDetailLevel,
                Target = "disk.deviceLatency.average",
                Detail = "Statistics level 1.",
            })),
        };

        var result = await Pipeline().RunAsync([limited], [], Fast, CancellationToken.None);

        Assert.Equal(HealthState.Warning, result.Health[0].Health);
        // The samples it could read are still useful and still returned.
        Assert.Single(result.Observations);
    }

    [Fact]
    public async Task Running_no_sources_is_not_an_error()
    {
        var result = await Pipeline().RunAsync([], [], Fast, CancellationToken.None);

        Assert.Empty(result.Batches);
        Assert.Empty(result.Observations);
        Assert.Empty(result.CollectionAlerts);
    }
}
