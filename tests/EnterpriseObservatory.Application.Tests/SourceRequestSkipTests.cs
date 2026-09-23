using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using Microsoft.Extensions.Time.Testing;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// F2's skip rule: while a source's read is still running — abandoned at its
/// own hard timeout, or simply overrunning — the next cycle for that source is
/// skipped rather than started on top of it, and the skip is counted.
/// docs/proposals/f-invert-collector-authority.md §8 decision 1.
/// </summary>
public class SourceRequestSkipTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }

    /// <summary>
    /// A source whose first read never returns on its own — it is completed
    /// only when the test calls <see cref="Release"/> — and whose later reads
    /// answer immediately. It never observes the cancellation token, the same
    /// way a vendor SDK that does not thread it through would not, so the
    /// runner cannot ask it to stop; only abandon it.
    /// </summary>
    private sealed class StuckThenFastSource(string id) : IObservationSource
    {
        private TaskCompletionSource<ObservationBatch>? _stuck;

        public string InstanceId { get; } = id;

        public int Attempts { get; private set; }

        public Task<ObservationBatch> ReadAsync(ObservationReadContext context, CancellationToken cancellationToken)
        {
            Attempts++;

            if (Attempts == 1)
            {
                _stuck = new TaskCompletionSource<ObservationBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _stuck.Task;
            }

            return Task.FromResult(Batch(InstanceId));
        }

        /// <summary>Lets the abandoned first read finally complete.</summary>
        public void Release() => _stuck?.TrySetResult(Batch(InstanceId));
    }

    /// <summary>A source that always answers immediately.</summary>
    private sealed class FastSource(string id) : IObservationSource
    {
        public string InstanceId { get; } = id;

        public Task<ObservationBatch> ReadAsync(ObservationReadContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Batch(InstanceId));
    }

    private static ObservationBatch Batch(string id) => new()
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
                    Raw = 1,
                    Rollup = RollupType.Average,
                    Interval = TimeSpan.FromSeconds(20),
                    Unit = "percent",
                },
            },
        ],
    };

    /// <summary>Pumps the fake clock until <paramref name="start"/>'s task completes.</summary>
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

    private static readonly CollectionPolicy AbandonQuickly = CollectionPolicy.Default with
    {
        SourceTimeout = TimeSpan.FromMilliseconds(200),
        MaxRetries = 0,
    };

    [Fact]
    public async Task A_still_running_read_causes_the_next_cycle_to_be_skipped_and_counted()
    {
        var time = new FakeTimeProvider();
        var source = new StuckThenFastSource("vc-1");
        var pipeline = new ObservationCollectionPipeline(new FixedClock(), time);

        // Cycle 1: the read never returns, so the runner abandons it at the
        // hard timeout. The task itself is still running afterwards.
        var first = await RunToCompletionAsync(
            time,
            () => pipeline.RunAsync([source], [], AbandonQuickly, CancellationToken.None),
            TimeSpan.FromMilliseconds(20));

        Assert.Empty(first.Batches);
        Assert.Equal(1, source.Attempts);

        // Cycle 2: due immediately, while the abandoned read is still inside
        // the source. It must not be asked again — that would put two calls
        // in one source instance — so this cycle is skipped, and the skip is
        // counted rather than merely logged.
        var second = await pipeline.RunAsync([source], first.Health, AbandonQuickly, CancellationToken.None);

        Assert.Equal(1, source.Attempts); // not asked again
        Assert.Empty(second.Batches);
        var health = Assert.Single(second.Health);
        Assert.Equal("vc-1", health.InstanceId);
        Assert.Equal(1, health.SkippedCycles);

        // A third cycle, still stuck, counts a second skip.
        var third = await pipeline.RunAsync([source], second.Health, AbandonQuickly, CancellationToken.None);
        Assert.Equal(1, source.Attempts);
        Assert.Equal(2, Assert.Single(third.Health).SkippedCycles);
    }

    [Fact]
    public async Task A_skipped_cycle_still_yields_a_state_for_that_source()
    {
        // Even though nothing was read this cycle, the source must not vanish
        // from the health report -- the caller has to be able to tell "we did
        // not look this cycle" from "this source was never configured".
        var time = new FakeTimeProvider();
        var source = new StuckThenFastSource("vc-1");
        var pipeline = new ObservationCollectionPipeline(new FixedClock(), time);

        var first = await RunToCompletionAsync(
            time,
            () => pipeline.RunAsync([source], [], AbandonQuickly, CancellationToken.None),
            TimeSpan.FromMilliseconds(20));

        var second = await pipeline.RunAsync([source], first.Health, AbandonQuickly, CancellationToken.None);

        Assert.Single(second.Health);
        Assert.Contains(second.Health, h => h.InstanceId == "vc-1");
    }

    [Fact]
    public async Task Once_the_abandoned_read_truly_finishes_the_source_is_read_again()
    {
        var time = new FakeTimeProvider();
        var source = new StuckThenFastSource("vc-1");
        var pipeline = new ObservationCollectionPipeline(new FixedClock(), time);

        var first = await RunToCompletionAsync(
            time,
            () => pipeline.RunAsync([source], [], AbandonQuickly, CancellationToken.None),
            TimeSpan.FromMilliseconds(20));

        var skipped = await pipeline.RunAsync([source], first.Health, AbandonQuickly, CancellationToken.None);
        Assert.Empty(skipped.Batches);

        // The read the runner walked away from finally comes back.
        source.Release();

        // The continuation that frees the source runs on the thread pool,
        // asynchronously to this test, so poll briefly rather than assume it
        // has already landed by the next statement.
        ObservationCycleResult? resumed = null;
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline)
        {
            resumed = await pipeline.RunAsync([source], skipped.Health, AbandonQuickly, CancellationToken.None);
            if (resumed.Batches.Count > 0)
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.NotNull(resumed);
        Assert.Single(resumed!.Batches);
        Assert.Equal(2, source.Attempts);
    }

    [Fact]
    public async Task One_source_stuck_and_skipped_does_not_hold_up_another()
    {
        var time = new FakeTimeProvider();
        var stuck = new StuckThenFastSource("vc-stuck");
        var healthy = new FastSource("vc-fast");

        var pipeline = new ObservationCollectionPipeline(new FixedClock(), time);

        // Warm up: abandon the stuck source once so it enters the "still
        // running" state the second cycle must skip.
        var first = await RunToCompletionAsync(
            time,
            () => pipeline.RunAsync([stuck], [], AbandonQuickly, CancellationToken.None),
            TimeSpan.FromMilliseconds(20));

        // This cycle reads both: the stuck one is skipped immediately, the
        // other is read normally and must not wait for it.
        var second = await pipeline.RunAsync(
            [stuck, healthy], first.Health, AbandonQuickly, CancellationToken.None);

        Assert.Single(second.Batches);
        Assert.Equal("vc-fast", second.Batches[0].SourceInstanceId);
        Assert.Contains(second.Health, h => h.InstanceId == "vc-stuck" && h.SkippedCycles == 1);
        Assert.Contains(second.Health, h => h.InstanceId == "vc-fast" && h.SkippedCycles == 0);
    }
}
