using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// The source-level half of the event collector contract suite (ADR-0025,
/// F1): what <see cref="EventCollectionPipeline"/> guarantees for any
/// <c>IEventSource</c> plugged into it, proven against today's vSphere source.
/// </summary>
public abstract class EventContractTests<TFixture>
    where TFixture : IEventContractFixture, new()
{
    // --- case: the event read goes through the runner ----------------------

    [Fact]
    public async Task The_event_read_goes_through_the_runner_a_rejected_login_trips_the_breaker_once_and_is_not_retried_and_a_timeout_is_counted()
    {
        var fixture = new TFixture();

        // A rejected login: asked once, and not again next cycle.
        var (rejecting, calls) = fixture.CreateRejectingLogin();
        var health = new HealthStore();
        var pipeline = new EventCollectionPipeline(new EventStore(), new TestClock(), health, MonitoringOptions.Default);

        var first = await pipeline.RunAsync([rejecting], CancellationToken.None);
        var second = await pipeline.RunAsync([rejecting], CancellationToken.None);

        Assert.Equal(1, calls());
        Assert.Equal(fixture.InstanceId, Assert.Single(first.Failures).Source);
        Assert.Equal(fixture.InstanceId, Assert.Single(second.Failures).Source);

        var refused = Assert.Single(health.Current);
        Assert.Equal(CollectorRole.Events, refused.Role);
        Assert.Equal(CollectionFailureKind.AuthenticationRejected, refused.LastFailureKind);
        Assert.Equal(1, refused.ConsecutiveFailures);
        Assert.True(refused.IsBackingOff);

        // A timeout: abandoned at the deadline, counted, and the cursor kept.
        var store = new EventStore();
        var slowHealth = new HealthStore();
        var slowPipeline = new EventCollectionPipeline(store, new TestClock(), slowHealth, MonitoringOptions.Default);

        var run = slowPipeline.RunAsync(
            [fixture.CreateSlow()], answered: null, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3))));

        var slow = await run;
        Assert.Equal(fixture.InstanceId, Assert.Single(slow.Failures).Source);
        Assert.Empty(store.Cursors);

        var timedOut = Assert.Single(slowHealth.Current);
        Assert.Equal(CollectorRole.Events, timedOut.Role);
        Assert.Equal(1, timedOut.ConsecutiveFailures);
        Assert.NotNull(timedOut.LastAttemptUtc);
    }

    private sealed class HealthStore : ICollectorHealthStore
    {
        private readonly Dictionary<(string, CollectorRole), CollectorHealth> _rows = [];

        public IReadOnlyList<CollectorHealth> Current => [.. _rows.Values];

        public void Merge(IReadOnlyList<CollectorHealth> health)
        {
            foreach (var h in health)
            {
                _rows[(h.InstanceId, h.Role)] = h;
            }
        }
    }

    /// <summary>The store's cursor rules, and nothing more.</summary>
    private sealed class EventStore : IEventStore
    {
        private readonly Dictionary<string, EventCursor> _cursors = new(StringComparer.Ordinal);

        public IReadOnlyList<EventCursor> Cursors => [.. _cursors.Values];

        public void Record(string sourceInstanceId, IReadOnlyList<SourceEvent> events, bool complete, DateTimeOffset readAtUtc) =>
            _cursors[sourceInstanceId] = new EventCursor
            {
                SourceInstanceId = sourceInstanceId,
                LastAttemptUtc = readAtUtc,
                LastSuccessUtc = readAtUtc,
            };

        public void RecordFailure(string sourceInstanceId, string detail, DateTimeOffset attemptedAtUtc) =>
            _cursors[sourceInstanceId] =
                (_cursors.GetValueOrDefault(sourceInstanceId) ?? new EventCursor { SourceInstanceId = sourceInstanceId })
                with { LastAttemptUtc = attemptedAtUtc, LastFailure = detail };

        public IReadOnlyList<SourceEvent> Recent(int limit, string? sourceInstanceId = null) => [];

        public int Prune(DateTimeOffset createdBeforeUtc) => 0;

        public IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) => [];
    }
}
