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
                Role = CollectorRole.Inventory,
                Health = HealthState.Unknown,
                ConsecutiveFailures = 5,
                LastSuccessUtc = T0.AddMinutes(-1),
                LastAttemptUtc = T0.AddMinutes(-1),
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
                Role = CollectorRole.Inventory,
                Health = HealthState.Unknown,
                ConsecutiveFailures = 5,
                LastSuccessUtc = T0.AddMinutes(-1),
                LastAttemptUtc = T0.AddMinutes(-1),
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
                Role = CollectorRole.Inventory,
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
                Role = CollectorRole.Inventory,
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

    // ---------------------------------------------------------------------
    // The cases below were all found against a live vCenter, not in a test.
    // The two breaker tests above passed throughout, because both described a
    // source that had SUCCEEDED a minute ago — the only situation the old
    // breaker ever opened in. Neither shape that actually occurs in the field
    // was ever constructed.
    // ---------------------------------------------------------------------

    /// <summary>An exception that knows retrying it cannot help.</summary>
    private sealed class RejectedLogin()
        : InvalidOperationException("vCenter rejected the credentials."), ICollectionFault
    {
        public CollectionFailureKind Kind => CollectionFailureKind.AuthenticationRejected;
    }

    [Fact]
    public async Task A_source_that_has_never_succeeded_is_still_held_off()
    {
        // A fresh installation with a wrong password never succeeds, so the
        // old rule ("keep the breaker closed until it has succeeded once")
        // meant it was never held off at all. That is not a slow recovery,
        // it is an unbounded retry loop against a directory that locks
        // accounts — on the one configuration every new customer starts from.
        var clock = new FixedClock(T0);
        var bad = new FakeSource("vc-1") { Behaviour = _ => throw new RejectedLogin() };

        var prior = new[]
        {
            new CollectorHealth
            {
                InstanceId = "vc-1",
                Role = CollectorRole.Inventory,
                Health = HealthState.Unknown,
                ConsecutiveFailures = 5,
                LastSuccessUtc = null,
                LastAttemptUtc = T0.AddSeconds(-30),
                LastFailureKind = CollectionFailureKind.AuthenticationRejected,
            },
        };

        var result = await Pipeline(clock).RunAsync([bad], prior, Fast, CancellationToken.None);

        Assert.Equal(0, bad.Attempts);
        Assert.True(result.Health[0].IsBackingOff);
    }

    [Fact]
    public async Task A_source_down_for_hours_is_held_off_rather_than_asked_every_cycle()
    {
        // The cooldown used to be measured from the last success, so the
        // longer a source had been down the less it was protected: after five
        // minutes of outage the breaker closed for good and the source was
        // asked on every cycle thereafter. Exactly backwards, and exactly what
        // the cooldown's own comment said it existed to prevent.
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
                Role = CollectorRole.Inventory,
                Health = HealthState.Unknown,
                ConsecutiveFailures = 240,
                LastSuccessUtc = T0.AddHours(-9),
                LastAttemptUtc = T0.AddSeconds(-30),
            },
        };

        var result = await Pipeline(clock).RunAsync([bad], prior, Fast, CancellationToken.None);

        Assert.Equal(0, bad.Attempts);
        Assert.True(result.Health[0].IsBackingOff);
    }

    [Fact]
    public async Task A_rejected_credential_is_asked_once_and_not_retried()
    {
        // The message the operator is shown says "This is not retried, so that
        // repeated attempts cannot lock the monitoring account out." It was
        // retried MaxRetries times inside the cycle, so the sentence was false
        // at the moment it was printed.
        var bad = new FakeSource("vc-1") { Behaviour = _ => throw new RejectedLogin() };

        await Pipeline().RunAsync([bad], [], Fast with { MaxRetries = 2 }, CancellationToken.None);

        Assert.Equal(1, bad.Attempts);
    }

    [Fact]
    public async Task A_rejected_credential_holds_the_source_off_after_one_failure()
    {
        // One strike, not five. Five rejected logins already exceeds vSphere
        // SSO's default lockout policy, so a threshold of five would lock the
        // account before the breaker ever protected it.
        var clock = new FixedClock(T0);
        var bad = new FakeSource("vc-1") { Behaviour = _ => throw new RejectedLogin() };
        var pipeline = Pipeline(clock);

        var first = await pipeline.RunAsync([bad], [], Fast, CancellationToken.None);
        var second = await pipeline.RunAsync(
            [bad], [.. first.Health], Fast, CancellationToken.None);

        Assert.Equal(1, bad.Attempts);
        Assert.True(second.Health[0].IsBackingOff);
    }

    [Fact]
    public async Task A_corrected_credential_is_picked_up_without_a_restart()
    {
        // The other half of the rule. Holding a source off forever because it
        // once had the wrong password would turn a five-minute fix into a
        // support call, so the hold expires and the classification is cleared
        // by the first success.
        var clock = new FixedClock(T0);
        var fixedNow = new FakeSource("vc-1");

        var prior = new[]
        {
            new CollectorHealth
            {
                InstanceId = "vc-1",
                Role = CollectorRole.Inventory,
                Health = HealthState.Unknown,
                ConsecutiveFailures = 1,
                LastSuccessUtc = null,
                LastAttemptUtc = T0.AddMinutes(-10),
                LastFailureKind = CollectionFailureKind.AuthenticationRejected,
            },
        };

        var result = await Pipeline(clock).RunAsync([fixedNow], prior, Fast, CancellationToken.None);

        Assert.Equal(1, fixedNow.Attempts);
        Assert.Equal(HealthState.Healthy, result.Health[0].Health);
        Assert.Null(result.Health[0].LastFailureKind);
    }

    [Fact]
    public async Task Every_thing_that_could_not_be_read_is_kept_not_just_the_first()
    {
        // The defect this replaces was invisible in the worst way: a collector
        // with three problems reported one, and the other two did not exist
        // anywhere — not in the API, not on screen, not in the database. It
        // took counting measurements against a real estate to notice that an
        // entire entity kind was unmeasured behind a message about a different
        // one.
        var partial = new FakeSource("vc-1")
        {
            Behaviour = _ => Task.FromResult(Ok(
                "vc-1",
                Failure("virtualDisk.totalLatency.average", "Counter is not defined."),
                Failure("datastore.totalLatency.average", "Counter is not defined."),
                Failure("cpu.costop.summation", "Statistics level too low."))),
        };

        var result = await Pipeline().RunAsync([partial], [], Fast, CancellationToken.None);

        var health = result.Health[0];

        Assert.Equal(HealthState.Warning, health.Health);
        Assert.Equal(3, health.PartialFailures.Count);
        Assert.Contains(health.PartialFailures, f => f.Target == "datastore.totalLatency.average");
    }

    [Fact]
    public async Task The_same_problem_on_two_hundred_entities_is_reported_once()
    {
        // One missing counter across an estate is one problem. Two hundred
        // identical lines is a list nobody reads, which is the same as not
        // reporting it.
        var repeated = new FakeSource("vc-1")
        {
            Behaviour = _ => Task.FromResult(Ok(
                "vc-1",
                [.. Enumerable.Range(0, 200).Select(_ =>
                    Failure("datastore.totalLatency.average", "Counter is not defined."))])),
        };

        var result = await Pipeline().RunAsync([repeated], [], Fast, CancellationToken.None);

        Assert.Single(result.Health[0].PartialFailures);
    }

    [Fact]
    public async Task What_could_not_be_read_names_what_it_was()
    {
        // "A counter is not defined on this vCenter" sends nobody anywhere.
        // The target is the half that turns it into a decision, and it was the
        // half being thrown away.
        var partial = new FakeSource("vc-1")
        {
            Behaviour = _ => Task.FromResult(Ok(
                "vc-1", Failure("datastore.totalLatency.average", "Counter is not defined."))),
        };

        var result = await Pipeline().RunAsync([partial], [], Fast, CancellationToken.None);

        Assert.Equal(
            "datastore.totalLatency.average",
            Assert.Single(result.Health[0].PartialFailures).Target);
    }

    [Fact]
    public async Task A_problem_that_has_been_fixed_stops_being_reported()
    {
        // A list that only grows is a list that stops being read.
        var source = new FakeSource("vc-1")
        {
            Behaviour = attempt => Task.FromResult(attempt == 1
                ? Ok("vc-1", Failure("a.counter", "Counter is not defined."))
                : Ok("vc-1")),
        };
        var pipeline = Pipeline();

        var first = await pipeline.RunAsync([source], [], Fast, CancellationToken.None);
        var second = await pipeline.RunAsync([source], [.. first.Health], Fast, CancellationToken.None);

        Assert.Single(first.Health[0].PartialFailures);
        Assert.Empty(second.Health[0].PartialFailures);
        Assert.Equal(HealthState.Healthy, second.Health[0].Health);
    }

    [Fact]
    public async Task A_source_that_could_not_be_reached_reports_no_partial_failures()
    {
        // These describe what a reachable source could not read. Carrying them
        // forward through an outage would state something we did not observe.
        var source = new FakeSource("vc-1")
        {
            Behaviour = attempt => attempt == 1
                ? Task.FromResult(Ok("vc-1", Failure("a.counter", "Counter is not defined.")))
                : throw new InvalidOperationException("connection refused"),
        };
        var pipeline = Pipeline();

        var first = await pipeline.RunAsync([source], [], Fast, CancellationToken.None);
        var second = await pipeline.RunAsync([source], [.. first.Health], Fast, CancellationToken.None);

        Assert.Empty(second.Health[0].PartialFailures);
        Assert.NotNull(second.Health[0].LastFailureDetail);
    }

    [Fact]
    public async Task A_success_clears_the_reason_the_previous_attempt_failed()
    {
        // Otherwise a working collector goes on explaining why it could not
        // authenticate yesterday.
        var source = new FakeSource("vc-1");

        var prior = new[]
        {
            new CollectorHealth
            {
                InstanceId = "vc-1",
                Role = CollectorRole.Inventory,
                Health = HealthState.Unknown,
                ConsecutiveFailures = 1,
                LastFailureDetail = "vCenter rejected the credentials.",
                LastAttemptUtc = T0.AddMinutes(-30),
            },
        };

        var result = await Pipeline().RunAsync([source], prior, Fast, CancellationToken.None);

        Assert.Null(result.Health[0].LastFailureDetail);
    }

    private static CollectionFailure Failure(string target, string detail) => new()
    {
        Kind = CollectionFailureKind.ProtocolError,
        Target = target,
        Detail = detail,
    };

    [Fact]
    public async Task An_unclassified_failure_is_still_retried()
    {
        // The forgiving direction, deliberately. An exception nobody
        // classified might be a reset connection, and refusing to retry those
        // would turn the fix for a lockout into a different outage.
        var bad = new FakeSource("ilo-1")
        {
            Behaviour = _ => throw new InvalidOperationException("connection reset"),
        };

        await Pipeline().RunAsync([bad], [], Fast with { MaxRetries = 2 }, CancellationToken.None);

        Assert.Equal(3, bad.Attempts);
    }
}
