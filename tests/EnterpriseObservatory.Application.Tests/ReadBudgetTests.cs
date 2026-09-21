using System.Diagnostics;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The read budget: how long a source may take, derived from how often it is
/// read, and what happens to the work done when the budget runs out (T1.1).
/// </summary>
public class ReadBudgetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }

    private sealed class TokenSource(string id, Func<int, CancellationToken, Task<ObservationBatch>> read)
        : IObservationSource
    {
        public string InstanceId { get; } = id;

        public int Attempts { get; private set; }

        public Task<ObservationBatch> ReadAsync(CancellationToken cancellationToken) =>
            read(++Attempts, cancellationToken);
    }

    private static ObservationBatch Partial(string id) => new()
    {
        SourceInstanceId = id,
        ReadAtUtc = T0,
        Observations =
        [
            new Observation
            {
                Entity = new EntityId("vm-1"),
                Source = id,
                SampledAtUtc = T0,
                Value = new CounterValue
                {
                    CounterName = "cpu.ready.summation",
                    Raw = 1,
                    Rollup = RollupType.Summation,
                    Interval = TimeSpan.FromSeconds(20),
                    Unit = "millisecond",
                },
            },
        ],
        Failures =
        [
            new CollectionFailure
            {
                Kind = CollectionFailureKind.Timeout,
                Target = "VirtualMachine",
                Detail = "1 of 2000 read before the budget ran out.",
            },
        ],
    };

    [Theory]
    [InlineData(30, 24)]
    [InlineData(300, 240)]
    [InlineData(60, 48)]
    public void The_timeout_is_derived_from_the_interval(int intervalSeconds, int expectedSeconds)
    {
        var policy = CollectionPolicy.Default.ForInterval(TimeSpan.FromSeconds(intervalSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.SourceTimeout);
    }

    [Fact]
    public void The_derived_timeout_never_falls_below_the_floor()
    {
        // ADR-0005 §5: derived from the poll interval, at least ten seconds.
        var policy = CollectionPolicy.Default.ForInterval(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(10), policy.SourceTimeout);
    }

    [Fact]
    public void Inventory_is_no_longer_held_to_the_metric_timeout()
    {
        // It used to share the single 25-second constant with metrics, on a
        // five-minute rhythm: a large estate had a tenth of its interval to
        // page through everything.
        var options = Monitoring.MonitoringOptions.Default;

        Assert.True(
            options.Collection.ForInterval(options.InventoryInterval).SourceTimeout >
            options.Collection.ForInterval(options.ObservationInterval).SourceTimeout);
    }

    [Fact]
    public async Task A_source_that_stops_at_the_deadline_keeps_what_it_read()
    {
        // Before, the cooperative token and the hard expiry fired at the same
        // instant, so a source that stopped when asked and handed back what it
        // had was abandoned anyway — its partial batch discarded with the rest.
        // A 2000-VM read that needs 42 s against 25 was therefore zero samples,
        // every cycle, rather than twenty-five seconds' worth.
        var source = new TokenSource("vc-1", async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                // Assembling what was read takes a moment.
                await Task.Delay(TimeSpan.FromMilliseconds(30), CancellationToken.None);
            }

            return Partial("vc-1");
        });

        var policy = CollectionPolicy.Default with { SourceTimeout = TimeSpan.FromSeconds(1), MaxRetries = 0 };

        var result = await new ObservationCollectionPipeline(new FixedClock())
            .RunAsync([source], [], policy, CancellationToken.None);

        Assert.Single(result.Batches);
        Assert.Single(result.Observations);
        Assert.Equal(HealthState.Warning, result.Health[0].Health);
        Assert.Contains(result.Health[0].PartialFailures, f => f.Kind == CollectionFailureKind.Timeout);
    }

    [Fact]
    public async Task Retries_share_one_budget_rather_than_each_getting_a_fresh_one()
    {
        // Three attempts at a full timeout each is three intervals: the cycle
        // waits for every source, so one slow vCenter held the whole metric
        // cycle for 75 seconds against a 30-second rhythm.
        var source = new TokenSource("vc-1", async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), token);
            throw new InvalidOperationException("transient");
        });

        var policy = CollectionPolicy.Default with
        {
            SourceTimeout = TimeSpan.FromMilliseconds(600),
            MaxRetries = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        };

        var watch = Stopwatch.StartNew();
        var result = await new ObservationCollectionPipeline(new FixedClock())
            .RunAsync([source], [], policy, CancellationToken.None);
        watch.Stop();

        Assert.Equal(2, source.Attempts);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1000), $"took {watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.Empty(result.Batches);
        Assert.Equal(HealthState.Unknown, result.Health[0].Health);
    }

    [Fact]
    public async Task A_source_cancelled_by_the_deadline_is_reported_as_out_of_time()
    {
        // Not as "The operation was canceled", which is what an operator was
        // shown before, and which reads like somebody pressed a button.
        var source = new TokenSource("vc-1", async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Partial("vc-1");
        });

        var policy = CollectionPolicy.Default with { SourceTimeout = TimeSpan.FromMilliseconds(300), MaxRetries = 2 };

        var result = await new ObservationCollectionPipeline(new FixedClock())
            .RunAsync([source], [], policy, CancellationToken.None);

        Assert.Equal(1, source.Attempts);
        Assert.Contains("within", result.Health[0].LastFailureDetail, StringComparison.Ordinal);
    }
}
