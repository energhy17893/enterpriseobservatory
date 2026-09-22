using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using Microsoft.Extensions.Time.Testing;

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

    /// <summary>
    /// Starts <paramref name="start"/> and drives <paramref name="time"/>
    /// forward in small virtual steps until the task it returns completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What decides pass or fail in these tests is entirely virtual: every
    /// delay, timeout and grace period the pipeline reasons about comes from
    /// <paramref name="time"/>, so <c>Advance</c> — not the wall clock — is
    /// what crosses a deadline or lets a retry proceed. That is what removes
    /// the dependence on real elapsed time that made these tests flaky under
    /// parallel load: the outcome no longer depends on how fast this machine
    /// happens to be right now.
    /// </para>
    /// <para>
    /// A due FakeTimeProvider callback still has to be dispatched and, under
    /// the test host, that dispatch is posted rather than guaranteed to run
    /// inline within <c>Advance</c> — so the loop below also yields real
    /// (negligible) time between steps to let it land. That yield decides
    /// nothing about the test's outcome, only how promptly this loop notices
    /// a virtual-time transition has already happened; the loop is bounded
    /// generously so it cannot hang.
    /// </para>
    /// </remarks>
    private static async Task<T> RunToCompletionAsync<T>(FakeTimeProvider time, Func<Task<T>> start, TimeSpan step)
    {
        var task = start();
        var deadline = Environment.TickCount64 + 10_000;

        while (!task.IsCompleted && Environment.TickCount64 < deadline)
        {
            time.Advance(step);
            await Task.Delay(1).ConfigureAwait(false);
        }

        Assert.True(task.IsCompleted, "Pipeline did not complete after pumping the fake clock forward.");
        return await task.ConfigureAwait(false);
    }

    [Fact]
    public async Task A_source_that_stops_at_the_deadline_keeps_what_it_read()
    {
        // Before, the cooperative token and the hard expiry fired at the same
        // instant, so a source that stopped when asked and handed back what it
        // had was abandoned anyway — its partial batch discarded with the rest.
        // A 2000-VM read that needs 42 s against 25 was therefore zero samples,
        // every cycle, rather than twenty-five seconds' worth.
        var time = new FakeTimeProvider();
        var source = new TokenSource("vc-1", async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Assembling what was read takes a moment.
                await Task.Delay(TimeSpan.FromMilliseconds(30), time, CancellationToken.None).ConfigureAwait(false);
            }

            return Partial("vc-1");
        });

        var policy = CollectionPolicy.Default with { SourceTimeout = TimeSpan.FromSeconds(1), MaxRetries = 0 };

        var pipeline = new ObservationCollectionPipeline(new FixedClock(), time);
        var result = await RunToCompletionAsync(
            time,
            () => pipeline.RunAsync([source], [], policy, CancellationToken.None),
            TimeSpan.FromMilliseconds(20));

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
        var time = new FakeTimeProvider();
        var source = new TokenSource("vc-1", async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), time, token).ConfigureAwait(false);
            throw new InvalidOperationException("transient");
        });

        var policy = CollectionPolicy.Default with
        {
            SourceTimeout = TimeSpan.FromMilliseconds(600),
            MaxRetries = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        };

        var start = time.GetUtcNow();
        var pipeline = new ObservationCollectionPipeline(new FixedClock(), time);
        var result = await RunToCompletionAsync(
            time,
            () => pipeline.RunAsync([source], [], policy, CancellationToken.None),
            TimeSpan.FromMilliseconds(20));
        var elapsed = time.GetUtcNow() - start;

        Assert.Equal(2, source.Attempts);
        // A fresh budget per retry would be three timeouts: close to 1800 ms
        // here. One shared budget keeps the whole thing under the timeout
        // plus a little, proving the sharing rather than merely asserting a
        // vague upper bound.
        Assert.True(elapsed < TimeSpan.FromMilliseconds(1000), $"took {elapsed.TotalMilliseconds:0} ms of virtual time");
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
