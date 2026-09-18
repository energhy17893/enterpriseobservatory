using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Tests;

public class InventoryCollectionPipelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FakeSource(string id) : IInventorySource
    {
        public string InstanceId { get; } = id;

        public int Attempts { get; private set; }

        public Func<int, Task<InventorySnapshot>>? Behaviour { get; init; }

        public Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            return Behaviour is null
                ? Task.FromResult(Ok(InstanceId))
                : Behaviour(Attempts);
        }
    }

    private static InventorySnapshot Ok(string id, params CollectionFailure[] failures) => new()
    {
        SourceInstanceId = id,
        ReadAtUtc = T0,
        Failures = failures,
    };

    // Retries are exercised in these tests, so the delay must not be real time.
    private static readonly CollectionPolicy Fast = CollectionPolicy.Default with
    {
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        SourceTimeout = TimeSpan.FromSeconds(2),
    };

    private static InventoryCollectionPipeline Pipeline(FixedClock? clock = null) =>
        new(clock ?? new FixedClock(T0));

    [Fact]
    public async Task A_source_that_answers_is_recorded_as_healthy()
    {
        var result = await Pipeline().RunAsync([new FakeSource("vc-1")], [], Fast, CancellationToken.None);

        Assert.Single(result.Snapshots);
        Assert.Equal(HealthState.Healthy, result.Health[0].Health);
        Assert.Empty(result.CollectionAlerts);
    }

    [Fact]
    public async Task One_source_failing_does_not_stop_the_others()
    {
        // The single most important property of the loop. A vendor outage must
        // cost that vendor's data, not everyone's.
        var good = new FakeSource("vc-1");
        var bad = new FakeSource("ilo-1")
        {
            Behaviour = _ => throw new InvalidOperationException("connection refused"),
        };

        var result = await Pipeline().RunAsync([good, bad], [], Fast, CancellationToken.None);

        Assert.Single(result.Snapshots);
        Assert.Equal("vc-1", result.Snapshots[0].SourceInstanceId);
        Assert.Contains(result.Health, h => h.InstanceId == "ilo-1" && h.Health == HealthState.Unknown);
    }

    [Fact]
    public async Task An_unreachable_source_is_unknown_not_critical()
    {
        // We do not know the estate is broken, only that we cannot see it.
        // Claiming more would be fabrication.
        var bad = new FakeSource("ilo-1")
        {
            Behaviour = _ => throw new InvalidOperationException("timeout"),
        };

        var result = await Pipeline().RunAsync([bad], [], Fast, CancellationToken.None);

        Assert.Equal(HealthState.Unknown, result.Health[0].Health);
        var alert = Assert.Single(result.CollectionAlerts);
        Assert.Contains("Unknown", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_failure_is_retried()
    {
        var flaky = new FakeSource("vc-1")
        {
            Behaviour = attempt => attempt < 3
                ? throw new InvalidOperationException("temporary")
                : Task.FromResult(Ok("vc-1")),
        };

        var result = await Pipeline().RunAsync([flaky], [], Fast, CancellationToken.None);

        Assert.Equal(3, flaky.Attempts);
        Assert.Single(result.Snapshots);
    }

    [Fact]
    public async Task Retries_are_bounded()
    {
        var alwaysBad = new FakeSource("vc-1")
        {
            Behaviour = _ => throw new InvalidOperationException("down"),
        };

        await Pipeline().RunAsync([alwaysBad], [], Fast with { MaxRetries = 2 }, CancellationToken.None);

        Assert.Equal(3, alwaysBad.Attempts);
    }

    [Fact]
    public async Task A_reported_failure_is_taken_at_face_value_and_not_retried()
    {
        // A source that says "authentication rejected" knows. Asking again will
        // not change the answer, and doing so on every cycle is how an account
        // gets locked out.
        var rejecting = new FakeSource("ilo-1")
        {
            Behaviour = _ => Task.FromResult(Ok("ilo-1", new CollectionFailure
            {
                Kind = CollectionFailureKind.AuthenticationRejected,
                Target = "ilo-1",
                Detail = "401",
            })),
        };

        var result = await Pipeline().RunAsync([rejecting], [], Fast, CancellationToken.None);

        Assert.Equal(1, rejecting.Attempts);
        // Reached but not fully read: degraded, not healthy.
        Assert.Equal(HealthState.Warning, result.Health[0].Health);
        Assert.Single(result.Snapshots);
    }

    [Fact]
    public async Task A_cooperative_source_is_cut_off_at_the_timeout()
    {
        var slow = new FakeSource("vc-1")
        {
            Behaviour = async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                return Ok("vc-1");
            },
        };

        var policy = Fast with { SourceTimeout = TimeSpan.FromMilliseconds(50), MaxRetries = 0 };

        var result = await Pipeline().RunAsync([slow], [], policy, CancellationToken.None);

        Assert.Empty(result.Snapshots);
        Assert.Equal(HealthState.Unknown, result.Health[0].Health);
    }

    [Fact]
    public async Task A_source_that_ignores_cancellation_is_abandoned_anyway()
    {
        // Requesting cancellation is not enough: a blocking socket read or a
        // third-party SDK that never threads the token through would otherwise
        // hold the cycle open indefinitely, which is the exact failure the
        // timeout exists to prevent.
        var uncooperative = new FakeSource("vc-1")
        {
            Behaviour = async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
                return Ok("vc-1");
            },
        };

        var policy = Fast with { SourceTimeout = TimeSpan.FromMilliseconds(50), MaxRetries = 0 };

        var started = DateTimeOffset.UtcNow;
        var result = await Pipeline().RunAsync([uncooperative], [], policy, CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"cycle took {elapsed.TotalSeconds:0.#}s");
        Assert.Empty(result.Snapshots);
        Assert.Equal(HealthState.Unknown, result.Health[0].Health);
    }

    [Fact]
    public async Task Consecutive_failures_accumulate_across_cycles()
    {
        var bad = new FakeSource("ilo-1")
        {
            Behaviour = _ => throw new InvalidOperationException("down"),
        };
        var pipeline = Pipeline();

        var health = Array.Empty<CollectorHealth>();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var result = await pipeline.RunAsync([bad], health, Fast, CancellationToken.None);
            health = [.. result.Health];
        }

        Assert.Equal(3, health[0].ConsecutiveFailures);
    }

    [Fact]
    public async Task The_breaker_opens_after_enough_consecutive_failures()
    {
        // An endpoint down for hours is not fixed by asking every 30 seconds.
        var clock = new FixedClock(T0);
        var bad = new FakeSource("ilo-1")
        {
            Behaviour = _ => throw new InvalidOperationException("down"),
        };

        var prior = new[]
        {
            new CollectorHealth
            {
                InstanceId = "ilo-1",
                Health = HealthState.Unknown,
                ConsecutiveFailures = 5,
                LastSuccessUtc = T0.AddMinutes(-1),
            },
        };

        var result = await Pipeline(clock).RunAsync([bad], prior, Fast, CancellationToken.None);

        Assert.Equal(0, bad.Attempts);
        Assert.True(result.Health[0].IsBackingOff);
    }

    [Fact]
    public async Task Backing_off_is_reported_so_it_is_not_mistaken_for_healthy()
    {
        // "We are not looking" and "we looked and it was fine" must never be
        // confused.
        var bad = new FakeSource("ilo-1")
        {
            Behaviour = _ => throw new InvalidOperationException("down"),
        };

        var prior = new[]
        {
            new CollectorHealth
            {
                InstanceId = "ilo-1",
                Health = HealthState.Unknown,
                ConsecutiveFailures = 5,
                LastSuccessUtc = T0.AddMinutes(-1),
            },
        };

        var result = await Pipeline().RunAsync([bad], prior, Fast, CancellationToken.None);

        Assert.Equal(HealthState.Unknown, result.Health[0].Health);
        var alert = Assert.Single(result.CollectionAlerts);
        Assert.Contains("backing off", alert.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_breaker_closes_again_once_the_cooldown_has_passed()
    {
        var clock = new FixedClock(T0.AddHours(1));
        var recovered = new FakeSource("ilo-1");

        var prior = new[]
        {
            new CollectorHealth
            {
                InstanceId = "ilo-1",
                Health = HealthState.Unknown,
                ConsecutiveFailures = 5,
                LastSuccessUtc = T0,
            },
        };

        var result = await Pipeline(clock).RunAsync([recovered], prior, Fast, CancellationToken.None);

        Assert.Equal(1, recovered.Attempts);
        Assert.Equal(HealthState.Healthy, result.Health[0].Health);
        Assert.Equal(0, result.Health[0].ConsecutiveFailures);
    }

    [Fact]
    public async Task A_source_that_never_succeeded_keeps_being_tried()
    {
        // Otherwise a source that was merely misconfigured at startup would
        // stay dark after someone fixes the configuration.
        var bad = new FakeSource("ilo-1")
        {
            Behaviour = _ => throw new InvalidOperationException("down"),
        };

        var prior = new[]
        {
            new CollectorHealth
            {
                InstanceId = "ilo-1",
                Health = HealthState.Unknown,
                ConsecutiveFailures = 99,
                LastSuccessUtc = null,
            },
        };

        var result = await Pipeline().RunAsync([bad], prior, Fast, CancellationToken.None);

        Assert.True(bad.Attempts > 0);
        Assert.Equal(100, result.Health[0].ConsecutiveFailures);
    }

    [Fact]
    public async Task Concurrency_is_capped()
    {
        // Opening fifty simultaneous sessions against one vCenter is a good way
        // to be throttled by it, or to become the reason it is slow.
        var concurrent = 0;
        var peak = 0;
        var padlock = new Lock();

        var sources = Enumerable.Range(0, 20).Select(i => new FakeSource($"s{i}")
        {
            Behaviour = async _ =>
            {
                lock (padlock)
                {
                    concurrent++;
                    peak = Math.Max(peak, concurrent);
                }

                await Task.Delay(20, CancellationToken.None);

                lock (padlock)
                {
                    concurrent--;
                }

                return Ok("s");
            },
        }).ToArray();

        await Pipeline().RunAsync(sources, [], Fast with { MaxConcurrency = 4 }, CancellationToken.None);

        Assert.True(peak <= 4, $"peak concurrency was {peak}");
    }

    [Fact]
    public async Task Running_no_sources_is_not_an_error()
    {
        var result = await Pipeline().RunAsync([], [], Fast, CancellationToken.None);

        Assert.Empty(result.Snapshots);
        Assert.Empty(result.Health);
        Assert.Empty(result.CollectionAlerts);
    }
}
