using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Host.AllInOne.State;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The two cycles running against one set of stores.
/// </summary>
/// <remarks>
/// These exercise the composition rather than any single class, because the
/// failures they cover only exist once the pieces are put together: each part
/// is correct alone and wrong in company. Every one was found by reading and
/// would have shipped silently — none produces an error, a log line or a
/// visibly odd screen.
/// </remarks>
public class MonitoringCycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly TestClock _clock = new(T0);
    private readonly InMemoryEntityGraphStore _graphs = new();
    private readonly InMemoryAlertStateStore _alerts = new();
    private readonly InMemoryCollectorHealthStore _health = new();
    private readonly RecordingNotifier _notifier = new();

    private static MonitoringOptions Options { get; } = new()
    {
        Collection = CollectionPolicy.Default with { MaxRetries = 0 },
    };

    private MonitoringCycle Cycle(IAlertNotifier? notifier = null) => new(
        new InventoryCollectionPipeline(_clock),
        new ObservationCollectionPipeline(_clock),
        _graphs,
        _alerts,
        _health,
        notifier ?? _notifier,
        _clock);

    // --- scoping ----------------------------------------------------------

    [Fact]
    public async Task The_metric_cycle_does_not_resolve_the_inventory_cycles_alerts()
    {
        // Reconciliation treats what it is given as the whole truth and closes
        // anything absent from it. The metric cycle runs ten times as often, so
        // without scoping it would clear every inventory alert seconds after it
        // was raised — and the alert would return on the next inventory pass,
        // so the inbox would blink rather than look broken.
        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow, alerts: [HardwareFault("vc-1")]),
        };

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow),
        };

        var cycle = Cycle();

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        Assert.Single(_alerts.All);

        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

            Assert.Single(result.Visible);
            Assert.Equal("Power supply failed", result.Visible[0].Title);
        }
    }

    [Fact]
    public async Task An_alert_the_inventory_cycle_stops_seeing_is_still_resolved()
    {
        // The scope must not become somewhere alerts go to be forgotten.
        var faulty = true;

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot(
                "vc-1", _clock.UtcNow, alerts: faulty ? [HardwareFault("vc-1")] : []),
        };

        var cycle = Cycle();

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        Assert.Single(_alerts.All);

        faulty = false;
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Empty(result.Visible);
    }

    [Fact]
    public async Task The_inbox_shows_both_cycles_at_once()
    {
        // An operator has one inbox. Whichever cycle ran last, it must show
        // everything currently wrong rather than that cycle's slice of it.
        // See ADR-0007.
        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow, alerts: [HardwareFault("vc-1")]),
        };

        // A metric source that cannot be reached raises a collection alert of
        // its own, in the other scope.
        var metrics = new FakeObservationSource("vc-2");

        var cycle = Cycle();

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        // Twice: an unreachable collector is a Warning, and hysteresis confirms
        // a warning on its second consecutive observation. One cycle of silence
        // was noise; two is a problem.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        _clock.Advance(TimeSpan.FromSeconds(30));
        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.Equal(2, result.Visible.Count);
    }

    // --- collector health -------------------------------------------------

    [Fact]
    public async Task Reading_inventory_and_reading_metrics_fail_independently()
    {
        // One vCenter, two collectors. Keyed by address alone, the
        // thirty-second cycle's successes keep resetting the five-minute
        // cycle's failure count, so a permanently broken inventory read never
        // reaches the circuit breaker.
        var inventory = new FakeInventorySource("vc-1"); // throws
        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow),
        };

        var cycle = Cycle();

        for (var i = 0; i < 3; i++)
        {
            await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromSeconds(30));
            await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        var inventoryHealth = HealthFor(CollectorRole.Inventory);
        var metricHealth = HealthFor(CollectorRole.Observation);

        Assert.Equal(3, inventoryHealth.ConsecutiveFailures);
        Assert.Equal(HealthState.Unknown, inventoryHealth.Health);

        Assert.Equal(0, metricHealth.ConsecutiveFailures);
        Assert.Equal(HealthState.Healthy, metricHealth.Health);
    }

    [Fact]
    public async Task The_two_roles_raise_separate_unreachable_alerts()
    {
        // "We cannot list your inventory" and "we cannot read your metrics" are
        // different problems with different fixes. One fingerprint for both
        // would also let each cycle resolve the other's alert.
        var inventory = new FakeInventorySource("vc-1");
        var metrics = new FakeObservationSource("vc-1");

        var cycle = Cycle();

        // Twice each: an unreachable collector is a Warning, which hysteresis
        // confirms on the second consecutive observation.
        for (var i = 0; i < 2; i++)
        {
            await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromSeconds(30));
            await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.Equal(2, result.Visible.Count);
        Assert.Equal(2, result.Visible.Select(a => a.Fingerprint).Distinct().Count());
    }

    // --- notification -----------------------------------------------------

    [Fact]
    public async Task A_problem_that_persists_notifies_once()
    {
        // The pending kind survives every subsequent observation, so an alert
        // that is never marked notified pages someone on every cycle for as
        // long as it fires.
        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow, alerts: [HardwareFault("vc-1")]),
        };

        var cycle = Cycle();

        for (var i = 0; i < 4; i++)
        {
            await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Single(_notifier.Dispatched);
    }

    [Fact]
    public async Task A_notification_that_could_not_be_sent_is_sent_again()
    {
        // At-least-once, deliberately. A duplicate page is an annoyance; a
        // dropped one is the failure the product exists to prevent.
        var notifier = new FailingNotifier();
        var cycle = Cycle(notifier);

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow, alerts: [HardwareFault("vc-1")]),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cycle.RunInventoryAsync([inventory], Options, CancellationToken.None));

        _clock.Advance(TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cycle.RunInventoryAsync([inventory], Options, CancellationToken.None));

        Assert.Equal(2, notifier.Calls);
    }

    // --- provenance -------------------------------------------------------

    [Fact]
    public async Task An_entity_that_stops_being_reported_vanishes()
    {
        // Vanishing depends on every entity carrying the id of the source that
        // reported it. No collector sets it, and nothing about a blank one
        // looks wrong: decommissioned hosts would simply stay in the graph
        // forever, and keep their last known colour.
        var present = true;

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot(
                "vc-1",
                _clock.UtcNow,
                entities: present ? [Host("vc-1/host-1", _clock.UtcNow)] : []),
        };

        var cycle = Cycle();

        var first = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        Assert.Equal(1, first.ActiveEntities);

        present = false;
        _clock.Advance(TimeSpan.FromMinutes(5));

        var second = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Equal(0, second.ActiveEntities);
        Assert.Equal(1, second.VanishedEntities);
    }

    [Fact]
    public async Task A_source_that_could_not_be_reached_does_not_make_its_entities_vanish()
    {
        var reachable = true;

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => reachable
                ? Snapshot("vc-1", _clock.UtcNow, entities: [Host("vc-1/host-1", _clock.UtcNow)])
                : throw new InvalidOperationException("unreachable"),
        };

        var cycle = Cycle();

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        reachable = false;
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        // Still active: we did not look, so we cannot say it is gone. The
        // collector alert already says we could not look.
        Assert.Equal(1, result.ActiveEntities);
        Assert.Contains("vc-1", result.SilentSources);
    }

    // --- helpers ----------------------------------------------------------

    private CollectorHealth HealthFor(CollectorRole role) =>
        _health.Current.Single(h => h.Role == role);

    private static InventorySnapshot Snapshot(
        string source,
        DateTimeOffset now,
        IReadOnlyList<Entity>? entities = null,
        IReadOnlyList<AlertDefinition>? alerts = null) => new()
        {
            SourceInstanceId = source,
            ReadAtUtc = now,
            Entities = entities ?? [],
            Alerts = alerts ?? [],
        };

    private static ObservationBatch Batch(string source, DateTimeOffset now) => new()
    {
        SourceInstanceId = source,
        ReadAtUtc = now,
    };

    private static Entity Host(string id, DateTimeOffset now) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.EsxiHost,
        DisplayName = id,
        Health = HealthState.Healthy,
        LastSeenUtc = now,
    };

    private static AlertDefinition HardwareFault(string source) => new()
    {
        Fingerprint = AlertFingerprint.Create(source, "Power supply failed", "Hardware", "psu-1", "psu"),
        Severity = AlertSeverity.Critical,
        Title = "Power supply failed",
        Description = "PSU 1 reports a fault.",
        Category = "Hardware",
        Source = source,
    };
}
