using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Analysis;
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
public class MonitoringCycleTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly TestClock _clock = new(T0);

    // The real stores against an in-memory database, not hand-written fakes.
    // A second implementation of these ports would be a second set of
    // semantics to keep in step, and the first thing to drift would be exactly
    // the subtleties these tests exist to pin down.
    private readonly InMemoryEntityGraphStore _graphs;
    private readonly InMemoryAlertStateStore _alerts;
    private readonly InMemoryCollectorHealthStore _health;
    private readonly InMemoryCoverageStore _coverage = new();
    private readonly InMemoryObservationStore _observations;
    private readonly InMemoryMaintenanceWindowStore _maintenance;
    private readonly RecordingNotifier _notifier = new();

    public MonitoringCycleTests()
    {
        _graphs = new InMemoryEntityGraphStore();
        _alerts = new InMemoryAlertStateStore();
        _health = new InMemoryCollectorHealthStore();
        _observations = new InMemoryObservationStore();
        _maintenance = new InMemoryMaintenanceWindowStore();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private static MonitoringOptions Options { get; } = new()
    {
        Collection = CollectionPolicy.Default with { MaxRetries = 0 },
    };

    private MonitoringCycle Cycle(
        IAlertNotifier? notifier = null,
        IObservationStore? observations = null,
        ICollectorHealthStore? health = null,
        IEntityGraphStore? graphs = null,
        IAlertStateStore? alerts = null,
        ICoverageStore? coverage = null) => new(
        new InventoryCollectionPipeline(_clock),
        new ObservationCollectionPipeline(_clock),
        graphs ?? _graphs,
        alerts ?? _alerts,
        health ?? _health,
        coverage ?? _coverage,
        notifier ?? _notifier,
        observations ?? _observations,
        _maintenance,
        _clock,
        new InMemoryEventStore());

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

    // --- measurements -----------------------------------------------------

    [Fact]
    public async Task What_the_cycle_samples_is_what_the_store_keeps()
    {
        // The link between collecting and keeping. Without it the product looks
        // healthy right up until somebody asks what happened yesterday.
        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations = [Reading("cpu.usage.average", 42.5, _clock.UtcNow)],
            },
        };

        await Cycle().RunObservationsAsync([metrics], Options, CancellationToken.None);

        var series = _observations.Query(new SeriesQuery
        {
            Key = new SeriesKey(new EntityId("vc-1:host-1"), "cpu.usage.average", string.Empty),
            FromUtc = T0.AddMinutes(-1),
            ToUtc = T0.AddMinutes(1),
            Resolution = SeriesResolution.Raw,
        });

        Assert.True(series.Exists);
        Assert.Equal(42.5, Assert.Single(series.Points).Last);
    }

    [Fact]
    public async Task Earlier_samples_are_kept_by_the_store_and_not_handed_to_the_rules()
    {
        // vCenter samples every twenty seconds and is asked every thirty, so a
        // read carries the sample before the current one. It belongs in the
        // history; it does not belong in front of a rule, which is handed one
        // value per series and means the current one.
        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations = [Reading("cpu.usage.average", 42.5, _clock.UtcNow)],
                Backfill = [Reading("cpu.usage.average", 17.0, _clock.UtcNow.AddSeconds(-20))],
            },
        };

        var result = await Cycle().RunObservationsAsync([metrics], Options, CancellationToken.None);

        var series = _observations.Query(new SeriesQuery
        {
            Key = new SeriesKey(new EntityId("vc-1:host-1"), "cpu.usage.average", string.Empty),
            FromUtc = T0.AddMinutes(-1),
            ToUtc = T0.AddMinutes(1),
            Resolution = SeriesResolution.Raw,
        });

        Assert.Equal([17.0, 42.5], series.Points.Select(p => p.Last));
        Assert.Equal(42.5, Assert.Single(result.Observations).Value.Raw);
    }

    [Fact]
    public async Task Capacity_read_with_the_inventory_is_kept_as_a_series()
    {
        // Roadmap M4.1. Capacity was read every inventory cycle, used for one
        // alert and dropped, so the product could say "95% full" and never
        // "and 80% a month ago". The inventory cycle now keeps it, in the same
        // store and under the same series identity as any metric.
        var datastore = new EntityId("vc-1:ds-1");

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow) with
            {
                Observations =
                [
                    // A source the collector forgot to name: the pipeline
                    // stamps provenance, the same as it does for entities.
                    CapacityCounters.Reading(
                        datastore, CapacityCounters.DatastoreUsed, 80e9, _clock.UtcNow, string.Empty),
                ],
            },
        };

        var result = await Cycle().RunInventoryAsync([inventory], Options, CancellationToken.None);

        var series = _observations.Query(new SeriesQuery
        {
            Key = new SeriesKey(datastore, CapacityCounters.DatastoreUsed, string.Empty),
            FromUtc = T0.AddMinutes(-1),
            ToUtc = T0.AddMinutes(1),
            Resolution = SeriesResolution.Raw,
        });

        Assert.True(series.Exists);
        Assert.Equal(80e9, Assert.Single(series.Points).Last);
        Assert.Equal(RollupType.Latest, series.Rollup);
        Assert.Equal("bytes", series.Unit);
        Assert.Equal("vc-1", Assert.Single(result.Observations).Source);
    }

    [Fact]
    public async Task A_capacity_write_that_fails_is_an_inventory_alert_not_a_lost_cycle()
    {
        // The inventory cycle's other writes are guarded so that one store
        // failing cannot cost the cycle its collection alerts, and this one
        // is too. It must not borrow the metric cycle's StorageFailure: that
        // describes the metric cycle's own samples.
        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow, alerts: [HardwareFault("vc-1")]) with
            {
                Observations =
                [
                    CapacityCounters.Reading(
                        new EntityId("vc-1:ds-1"), CapacityCounters.DatastoreFree, 1e9, _clock.UtcNow, "vc-1"),
                ],
            },
        };

        var result = await Cycle(observations: new FailingObservationStore())
            .RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Contains(result.Visible, a => a.Title == "Power supply failed");

        // Recorded, in the scope that can resolve it. A warning waits for
        // confirmation before it is shown, so it is looked for in the store.
        Assert.Contains(_alerts.All, a =>
            a.Title == "State could not be saved" && a.Scope == AlertScopes.Inventory);
        Assert.Null(result.StorageFailure);
    }

    [Fact]
    public async Task A_storage_failure_does_not_stop_the_cycle()
    {
        // Losing a sample costs one point on one chart and the next cycle
        // replaces it. A collection loop that stopped because the disk was busy
        // is a blind monitoring system, which is very much worse.
        var cycle = new MonitoringCycle(
            new InventoryCollectionPipeline(_clock),
            new ObservationCollectionPipeline(_clock),
            _graphs,
            _alerts,
            _health,
            _coverage,
            _notifier,
            new FailingObservationStore(),
            _maintenance,
            _clock,
            new InMemoryEventStore());

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations = [Reading("cpu.usage.average", 1, _clock.UtcNow)],
            },
        };

        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        // Reported rather than swallowed: "we are collecting but not keeping"
        // has to be visible in the product.
        Assert.NotNull(result.StorageFailure);
        Assert.Single(result.Observations);
    }

    [Fact]
    public async Task A_storage_failure_that_clears_stops_being_reported()
    {
        // Postgres restarts once at 02:00 and one append throws. Left uncleared,
        // that message rides every later result until the service is restarted
        // and the worker warns every thirty seconds that samples could not be
        // recorded -- while they are being recorded perfectly. The cost is not
        // the noise. It is that the operator is trained to ignore the one
        // message that means the history really does have a hole, so the night
        // the disk actually fills nobody looks.
        var store = new FlakyObservationStore { Fails = true };
        var cycle = Cycle(observations: store);

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations = [Reading("cpu.usage.average", 1, _clock.UtcNow)],
            },
        };

        var failed = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        Assert.NotNull(failed.StorageFailure);

        store.Fails = false;
        _clock.Advance(TimeSpan.FromSeconds(30));

        var recovered = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.Null(recovered.StorageFailure);
    }

    [Fact]
    public async Task A_cycle_with_nothing_to_store_does_not_claim_the_samples_are_kept()
    {
        // The other half of the same decision, and it has to go the other way.
        // An empty batch never reaches the store, so nothing is learned about
        // whether the store works; clearing on the way past would announce that
        // the samples are being kept on the strength of an attempt never made.
        // A vCenter that goes silent after a failed write would then close the
        // report of that write, and the gap left at 02:00 would have nothing
        // anywhere in the product still pointing at it.
        var store = new FlakyObservationStore { Fails = true };
        var cycle = Cycle(observations: store);
        var sampling = true;

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations = sampling
                    ? [Reading("cpu.usage.average", 1, _clock.UtcNow)]
                    : [],
            },
        };

        var failed = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        Assert.NotNull(failed.StorageFailure);

        // Reachable, and with nothing to say. The store is never touched.
        sampling = false;
        store.Fails = false;
        _clock.Advance(TimeSpan.FromSeconds(30));

        var quiet = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.Equal(failed.StorageFailure, quiet.StorageFailure);
    }

    // --- the cycle's own store writes -------------------------------------

    [Fact]
    public async Task A_health_write_that_fails_does_not_cost_the_cycle_its_samples()
    {
        // The pool is exhausted, or a statement times out, while the collectors'
        // health is being merged. Every source has already answered and the
        // samples are in memory -- and unguarded, the pass dies there, before
        // the one piece of code written to keep those samples is reached. What
        // the estate gets is a thirty-second hole in every chart and a log line
        // on a server nobody is reading at the time.
        var cycle = Cycle(health: new FailingCollectorHealthStore());

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations = [Reading("cpu.usage.average", 42.5, _clock.UtcNow)],
            },
        };

        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.Single(result.Observations);

        // Null rather than merely present: the append ran and succeeded, so the
        // sample is on disk and not only in the result.
        Assert.Null(result.StorageFailure);

        // And the operator is told, because a collector whose health stopped
        // being written is a circuit breaker that stops counting -- a source
        // broken for a day would be polled every thirty seconds forever.
        Assert.Contains(_alerts.All, a => a.Title == "State could not be saved");
    }

    [Fact]
    public async Task A_topology_write_that_fails_does_not_silence_the_collection_alerts()
    {
        // The worse one, and it is an ordering accident: the graph is written
        // before reconciliation. A throw there took the whole inventory pass
        // with it, so a failure to persist topology also suppressed that
        // cycle's collection alerts -- the product went quiet about a vCenter
        // it could not reach because a different subsystem could not write.
        // Quiet is exactly what "everything is fine" looks like.
        var cycle = Cycle(graphs: new FailingEntityGraphStore());
        var inventory = new FakeInventorySource("vc-1"); // throws

        // Twice: both of these are Warnings, and hysteresis confirms a warning
        // on its second consecutive observation.
        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Contains(result.Visible, a => a.Title == "Collector unreachable (inventory)");

        // In the product rather than only in a log, for the reason ADR-0005
        // gives: the topology on screen is now older than the estate, and
        // nothing else on the screen says so.
        Assert.Contains(result.Visible, a => a.Title == "State could not be saved");
    }

    [Fact]
    public async Task The_two_cycles_report_their_own_write_failures_separately()
    {
        // Both cycles merge collector health, into one alert store, on
        // different clocks. One fingerprint for both would put two rows
        // carrying the same identity in one inbox -- and acknowledging either
        // would land on whichever the store reached first, so the button would
        // appear to work and an identical alert would stay open beside it.
        var cycle = Cycle(health: new FailingCollectorHealthStore());

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow),
        };

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow),
        };

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        var failures = _alerts.All.Where(a => a.Title == "State could not be saved").ToList();

        Assert.Equal(2, failures.Count);
        Assert.Equal(2, failures.Select(a => a.Fingerprint).Distinct().Count());
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
                entities: present ? [Host("vc-1:host-1", _clock.UtcNow)] : []),
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
                ? Snapshot("vc-1", _clock.UtcNow, entities: [Host("vc-1:host-1", _clock.UtcNow)])
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

    // --- the two cycles at the same moment ---------------------------------

    [Fact]
    public async Task Collector_health_survives_both_cycles_reading_and_merging_at_once()
    {
        // The two cycles are independent tasks on different schedules, and each
        // one reads Current and then calls Merge. Nothing in the suite had ever
        // run them at the same moment, so an unguarded dictionary here looked
        // fine for as long as nobody looked.
        //
        // The throw is the outcome to hope for. The quiet one is a read that
        // comes back short: a source missing from the list is a source
        // SourceRunner has never seen, so the circuit breaker is handed a fresh
        // CollectorHealth with no failures and starts again from zero against a
        // vCenter that has been refusing us since this morning. An account that
        // was merely rate-limited is then locked out by the monitoring product,
        // which is the failure this product is least allowed to cause.
        var store = new InMemoryCollectorHealthStore();
        var reading = true;

        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (Volatile.Read(ref reading))
            {
                // Enumerated, not just counted: the resize happens under the
                // enumerator, which is where an unguarded read falls over.
                foreach (var entry in store.Current)
                {
                    Assert.NotEmpty(entry.InstanceId);
                }
            }
        })).ToList();

        var writers = Enumerable.Range(0, 2).Select(cycle => Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                store.Merge([Unreachable($"vc-{cycle}-{i}")]);
            }
        })).ToList();

        await Task.WhenAll(writers);
        Volatile.Write(ref reading, false);
        await Task.WhenAll(readers);

        // Every merge landed, which the lock also has to leave true: a guard
        // that dropped writes would pass the paragraph above and fail the
        // product in the same way a short read does.
        Assert.Equal(4_000, store.Current.Count);
    }

    /// <summary>One collector that is failing, as the runner would report it.</summary>
    /// <remarks>
    /// Failures and a backoff rather than a healthy record, because the field
    /// this test is protecting is <c>ConsecutiveFailures</c>: a read that misses
    /// this entry is a read that resets it.
    /// </remarks>
    private static CollectorHealth Unreachable(string instanceId) => new()
    {
        InstanceId = instanceId,
        Role = CollectorRole.Observation,
        Health = HealthState.Critical,
        ConsecutiveFailures = 47,
        IsBackingOff = true,
        LastFailureDetail = "Cannot complete login due to an incorrect user name or password.",
        LastFailureKind = CollectionFailureKind.AuthenticationRejected,
    };

    // --- helpers ----------------------------------------------------------

    private CollectorHealth HealthFor(CollectorRole role) =>
        _health.Current.Single(h => h.Role == role);

    private static InventorySnapshot Snapshot(
        string source,
        DateTimeOffset now,
        IReadOnlyList<Entity>? entities = null,
        IReadOnlyList<AlertDefinition>? alerts = null,
        IReadOnlyList<Relationship>? relationships = null) => new()
        {
            SourceInstanceId = source,
            ReadAtUtc = now,
            Entities = entities ?? [],
            Alerts = alerts ?? [],
            Relationships = relationships ?? [],
        };

    // --- rules on measurements --------------------------------------------

    [Fact]
    public async Task A_fault_counter_reading_becomes_an_alert_in_the_metric_scope()
    {
        // End to end through the real cycle, because on the live estate this
        // rule has never fired: every one of 670,514 fault-counter readings is
        // zero, which is the correct answer for a clean fabric and also
        // exactly what a broken rule looks like. Passing unit tests and a
        // quiet inbox are the two things this product has been burned by
        // together.
        var cycle = Cycle();

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations = [Fault("storagePath.busResets.summation", 2, "vmhba1:C0:T3:L7")],
            },
        };

        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        var alert = Assert.Single(_alerts.All, a => a.Category == "Fault");

        Assert.Equal(AlertScopes.Observation, alert.Scope);
        Assert.Contains("vmhba1:C0:T3:L7", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rule_raises_its_alert_into_the_metric_scope_before_any_store_touches_it()
    {
        // The test above reads the scope back out of the store, and for as
        // long as the stores stamped what they filed it would have read
        // correctly even if the cycle had handed the alert over with no scope
        // at all -- which it did, for every fault-counter and peer-outlier
        // alert, while the database row beside it carried "observation" from
        // the store's own argument. The stores no longer stamp, so that hole
        // is closed at the source; this assertion is kept anyway because it is
        // taken from the reconciliation result itself, before any store has
        // seen it. That is what the worker returns and what the notifier is
        // handed, so an alert that is only scoped once it has been stored
        // would still be dispatched unscoped -- and a notification routed by
        // scope would go to the wrong place while every screen looked right.
        var watcher = new ScopeWatchingAlertStateStore(_alerts);
        var cycle = Cycle(alerts: watcher);

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations = [Fault("storagePath.busResets.summation", 2, "vmhba1:C0:T3:L7")],
            },
        };

        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        var reconciled = Assert.Single(watcher.Reconciled, a => a.Category == "Fault");

        Assert.Equal(AlertScopes.Observation, reconciled.Scope);
    }

    [Fact]
    public async Task A_write_the_inventory_cycle_could_not_make_is_owned_by_the_inventory_scope()
    {
        // The rules are not the only unscoped source. Guarded produces its
        // "State could not be saved" definition with no scope either, and the
        // inventory cycle is where two of them are raised -- so the same drift
        // lives on this side, and nothing pointed at it: every other alert this
        // cycle reconciles was stamped by the pipeline on the way in, which is
        // what made the gap invisible. An operator reading which evaluation
        // owns a failed write would be told one thing by the running process
        // and another by the same process after a restart.
        var watcher = new ScopeWatchingAlertStateStore(_alerts);
        var cycle = Cycle(health: new FailingCollectorHealthStore(), alerts: watcher);

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow, entities: [Host("vc-1:host-1", _clock.UtcNow)]),
        };

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        var reconciled = Assert.Single(
            watcher.Reconciled, a => a.Title == "State could not be saved");

        Assert.Equal(AlertScopes.Inventory, reconciled.Scope);
    }

    [Fact]
    public void A_store_files_an_alert_exactly_as_the_reconciler_decided_it()
    {
        // The other half, and the half that makes a restart agree with itself.
        // Both stores used to re-stamp Scope from their own argument, which is
        // why the cycle could hand over unscoped alerts for months without
        // anyone noticing: the answer was corrected on the way past. Neither
        // does now, and this pins that -- a store handed an instance with the
        // wrong scope must file the wrong scope, loudly, rather than quietly
        // making the layer above it look right. If this starts failing because
        // a store has begun stamping again, every test that asserts a scope
        // becomes a test of the store rather than of the cycle, and the drift
        // this design removed can come back invisibly.
        var wrong = new AlertInstance
        {
            Fingerprint = AlertFingerprint.Create("vc-1", "Filed by hand", "Hardware", "esx-01"),
            Severity = AlertSeverity.Critical,
            State = AlertLifecycleState.Open,
            Title = "Filed by hand",
            ConsecutiveHits = 1,
            IsConfirmed = true,
            ClearedByOperator = false,
            PendingNotification = AlertNotificationKind.None,
            FirstSeenUtc = _clock.UtcNow,
            LastSeenUtc = _clock.UtcNow,
            Scope = AlertScopes.Observation,
        };

        _alerts.Reconcile(
            AlertScopes.Inventory,
            (_, _) => new AlertReconciliationResult { Instances = [wrong] });

        var filed = Assert.Single(_alerts.InstancesIn(AlertScopes.Inventory));

        Assert.Equal(AlertScopes.Observation, filed.Scope);
    }

    [Fact]
    public void A_cycle_that_stamps_nothing_still_produces_scoped_alerts()
    {
        // The reconciler is the only stamp left, so this is the assertion the
        // whole design rests on: an alert whose definition names no scope, run
        // through a store that no longer corrects anything, still comes out
        // owned by the evaluation that reconciled it. Remove the stamp from
        // AlertReconciler and this fails -- which is the property that makes a
        // third store, or a fourth caller, unable to get the field wrong.
        var definition = HardwareFault("vc-1") with { Scope = string.Empty };

        _alerts.Reconcile(AlertScopes.Inventory, (stored, flaps) => AlertReconciler.Reconcile(
            new AlertReconciliationRequest
            {
                Scope = AlertScopes.Inventory,
                Observed = [definition],
                Stored = stored,
                FlapHistories = flaps,
                NowUtc = _clock.UtcNow,
            }));

        var filed = Assert.Single(_alerts.InstancesIn(AlertScopes.Inventory));

        Assert.Equal(AlertScopes.Inventory, filed.Scope);
    }

    [Fact]
    public async Task A_fault_that_stops_being_reported_is_resolved()
    {
        // Reconciliation treats each pass as the whole truth, so a transient
        // fault opens and then closes. That is honest rather than unfortunate:
        // a path that does it repeatedly is caught by flap detection, which
        // exists because such a problem otherwise leaves no instance behind.
        var cycle = Cycle();
        var faulty = true;

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations = faulty
                    ? [Fault("storagePath.busResets.summation", 1, "vmhba0:C0:T0:L1")]
                    : [],
            },
        };

        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        Assert.Contains(_alerts.All, a => a.Category == "Fault" && a.State != AlertLifecycleState.Resolved);

        faulty = false;
        _clock.Advance(TimeSpan.FromSeconds(30));
        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.DoesNotContain(
            _alerts.All,
            a => a.Category == "Fault" && a.State != AlertLifecycleState.Resolved);
    }

    [Fact]
    public async Task A_source_that_goes_silent_in_the_metric_cycle_keeps_its_metric_alerts()
    {
        // The same defect PR #37 fixed for the inventory cycle, in the other
        // scope. EntityGraph.Merge keeps a silent source's entities because we
        // did not look; a metric-scope alert on one of them must be held the
        // same way. It was not: RunObservationsAsync reconciles with
        // CarryForward([], null, unevaluated), so a source that fails to
        // answer this cycle has its open fault alerts resolved -- not because
        // the fault cleared, but because nobody looked -- and they reopen and
        // notify again the moment the source answers.
        var reachable = true;

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow, entities: [Host("vc-1:host-1", _clock.UtcNow)]),
        };

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => reachable
                ? Batch("vc-1", _clock.UtcNow) with
                {
                    Observations = [Fault("storagePath.busResets.summation", 1, "vmhba0:C0:T0:L1")],
                }
                : throw new InvalidOperationException("unreachable"),
        };

        var cycle = Cycle();

        // The entity has to be in the graph, carrying its source, before
        // absence can be told apart from a source nobody has ever asked.
        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        // Twice: a Warning confirms on its second consecutive observation.
        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        var raised = Assert.Single(_alerts.All, a => a.Category == "Fault");
        Assert.Equal(AlertLifecycleState.Open, raised.State);

        reachable = false;
        _clock.Advance(TimeSpan.FromSeconds(30));
        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.Contains("vc-1", result.SilentSources);

        var held = Assert.Single(_alerts.All, a => a.Category == "Fault");
        Assert.Equal(AlertLifecycleState.Open, held.State);
        Assert.Equal(raised.LastSeenUtc, held.LastSeenUtc);

        // Held, not carried as a fresh confirmation: nothing about it is owed
        // a notification just because the cycle ran.
        Assert.DoesNotContain(result.ToNotify, a => a.Category == "Fault");

        // And once the source answers again without the fault, it resolves as
        // before -- this is not a scope that stopped resolving anything.
        reachable = true;
        _clock.Advance(TimeSpan.FromSeconds(30));
        var recovered = await cycle.RunObservationsAsync(
            [new FakeObservationSource("vc-1") { Behaviour = () => Batch("vc-1", _clock.UtcNow) }],
            Options,
            CancellationToken.None);

        Assert.DoesNotContain(recovered.Visible, a => a.Category == "Fault");
    }

    [Fact]
    public async Task A_clean_fabric_raises_nothing()
    {
        // The live case. Zero readings across every path, and the rule must
        // stay silent -- silence that is correct rather than silence that is
        // the rule failing to run, which is why the two tests above exist.
        var cycle = Cycle();

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations =
                [
                    Fault("storagePath.busResets.summation", 0, "vmhba0:C0:T0:L1"),
                    Fault("storagePath.commandsAborted.summation", 0, "vmhba0:C0:T0:L1"),
                ],
            },
        };

        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.DoesNotContain(_alerts.All, a => a.Category == "Fault");
    }

    [Fact]
    public async Task A_rule_that_throws_does_not_cost_the_cycle_its_other_work()
    {
        // GuardedRuleTests proves the guard catches. Nothing proved the cycle
        // puts its rules behind it, and that is the half that can be removed
        // in silence: unwrap either call in RunObservationsAsync and every one
        // of those unit tests still passes. What the estate would get instead
        // is one unreadable sample ending the whole pass -- the collection
        // alert never reconciled, its notification never sent, the peer
        // comparison never run -- and an inbox that looks calm because nothing
        // survived long enough to be reported.
        var cycle = Cycle();

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations =
                [
                    Sourceless(),
                    Vantage("esx01", 0),
                    Vantage("esx02", 0),
                    Vantage("esx03", 12),
                ],
                Failures =
                [
                    new CollectionFailure
                    {
                        Kind = CollectionFailureKind.InsufficientDetailLevel,
                        Target = "disk.deviceLatency.average",
                        Detail = "Statistics level 1; level 2 required.",
                    },
                ],
            },
        };

        // Twice: all three of these are Warnings, and hysteresis confirms a
        // warning on its second consecutive observation.
        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        // What the source managed to say about itself still arrives. This is
        // the work the broken rule must not cost: a configuration problem an
        // operator can actually fix, lost because an unrelated rule tripped.
        Assert.Contains(result.Visible, a => a.Title == "Platform detail level too low");

        // The blind spot, named. Without the rule's id an operator reads the
        // missing findings as good news and has nothing to hand support.
        var failure = Assert.Single(result.Visible, a => a.Title == "Analysis rule failed");
        Assert.Contains(FaultCounters.RuleId, failure.Description, StringComparison.Ordinal);

        // The sibling still ran. Guarding each rule separately rather than the
        // block of them is the whole point -- one try around both would let
        // the first failure silence the storage-path diagnosis, which is the
        // rule this product was built to deliver.
        Assert.Contains(result.Visible, a => a.Title == "Slow from one host only");
    }

    [Fact]
    public async Task The_layer_rule_is_reached_and_names_the_layer_it_blames()
    {
        // Per-device latency is the product's self-described diagnostic core
        // and for months nothing read it. This drives the rule through the
        // real cycle rather than trusting its unit tests: device latency far
        // above kernel and queue is the array, and the title has to say so,
        // because the title is what tells an operator which console to open.
        var cycle = Cycle();

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations =
                [
                    Layer("disk.deviceLatency.average", 20),
                    Layer("disk.kernelLatency.average", 1),
                    Layer("disk.queueLatency.average", 1),
                ],
            },
        };

        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.Contains(result.Visible, a => a.Title == "Array or fabric is the bottleneck");
        Assert.DoesNotContain(result.Visible, a => a.Title == "Analysis rule failed");
    }

    [Fact]
    public async Task The_cpu_rule_is_reached_and_is_given_the_graph_it_needs()
    {
        // This rule cannot answer anything without the entity graph: it
        // compares a machine against its siblings on the same host, and the
        // siblings are reachable only through the VM RunsOn Host edges the
        // inventory cycle builds. Wiring it with the observations alone would
        // compile, run, and silently find nothing forever -- so the inventory
        // cycle runs first here on purpose.
        var cycle = Cycle();

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot(
                "vc-1",
                _clock.UtcNow,
                entities:
                [
                    Node("vc-1:host-1", EntityKind.EsxiHost, "esx01"),
                    .. WaitingGuests.Select(v => Node(v, EntityKind.VirtualMachine, v)),
                ],
                relationships:
                [
                    .. WaitingGuests.Select(v => new Relationship
                    {
                        From = new EntityId(v),
                        To = new EntityId("vc-1:host-1"),
                        Kind = RelationshipKind.RunsOn,
                        ObservedAtUtc = _clock.UtcNow,
                    }),
                ]),
        };

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations =
                [
                    Busy("vc-1:host-1", 92),
                    .. WaitingGuests.Select(v => Waiting(v, 4000)),
                ],
            },
        };

        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        // The host verdict, not the per-machine one: a saturated host with
        // several machines waiting is a host problem, and saying so is the
        // whole reason the rule computes both before choosing.
        Assert.Contains(result.Visible, a => a.Entity == new EntityId("vc-1:host-1"));
        Assert.DoesNotContain(result.Visible, a => a.Title == "Analysis rule failed");
    }

    /// <summary>Three guests, so one of them can be compared with its siblings.</summary>
    private static readonly string[] WaitingGuests = ["vc-1:vm-1", "vc-1:vm-2", "vc-1:vm-3"];

    /// <summary>
    /// A graph node. Virtual machines carry a width, because CpuContention
    /// divides waiting time by it and declines to judge a machine whose width
    /// it cannot read -- so a guest with no sizing reaches no verdict, and a
    /// wiring test built from one would pass whether or not the rule is
    /// called.
    /// </summary>
    private static Entity Node(string id, EntityKind kind, string name) => new()
    {
        Id = new EntityId(id),
        Kind = kind,
        DisplayName = name,
        SourceInstanceId = "vc-1",
        LastSeenUtc = T0,
        Sizing = kind == EntityKind.VirtualMachine
            ? new EntitySizing { VirtualCpuCount = 1 }
            : null,
    };

    /// <summary>One layer of one device's latency, as the host reports it.</summary>
    private static Observation Layer(string counter, double ms) => new()
    {
        Entity = new EntityId("vc-1:host-1"),
        SampledAtUtc = T0,
        Source = "vc-1",
        Value = new CounterValue
        {
            CounterName = counter,
            Raw = ms,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "millisecond",
            Instance = "naa.600508b1001c",
        },
    };

    private static Observation Busy(string host, double percent) => new()
    {
        Entity = new EntityId(host),
        SampledAtUtc = T0,
        Source = "vc-1",
        Value = new CounterValue
        {
            CounterName = "cpu.usage.average",
            Raw = percent,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "percent",
        },
    };

    /// <summary>Ready milliseconds, which only mean anything against the interval.</summary>
    private static Observation Waiting(string vm, double ms) => new()
    {
        Entity = new EntityId(vm),
        SampledAtUtc = T0,
        Source = "vc-1",
        Value = new CounterValue
        {
            CounterName = "cpu.ready.summation",
            Raw = ms,
            Rollup = RollupType.Summation,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "millisecond",
        },
    };

    /// <summary>Three hosts: the fewest that can agree about a shared volume.</summary>
    private static readonly string[] MountingHosts = ["esx01", "esx02", "esx03"];

    // --- the rules are reached at all -------------------------------------

    [Fact]
    public async Task Every_rule_the_cycle_guards_can_reach_the_inbox()
    {
        // Four rules were written, tested and merged before anything called
        // them. A rule that is never invoked is indistinguishable from a rule
        // that found nothing -- which is precisely the failure this suite
        // already caught once, when GuardedRule had eight passing unit tests
        // and no test of its call site. These fixtures make two of the four
        // fire through the real cycle; the wiring block is shared, so a rule
        // dropped from it takes its title with it.
        var cycle = Cycle();

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations =
                [
                    // Slow from every host that mounts it, and carrying load:
                    // the array, not one path. PeerOutliers must stay quiet
                    // here and SharedVolumeLatency must speak.
                    .. MountingHosts.Select(h => Vantage(h, 9)),
                    .. MountingHosts.Select(h => Demand(h, 40)),

                    // A second volume that is busy and reads exactly zero with
                    // SIOC inactive -- the estate cannot measure it at all.
                    .. MountingHosts
                        .Select(h => Vantage(h, 0, "vc-1:ds-blind")),
                    .. MountingHosts
                        .Select(h => Demand(h, 40, "vc-1:ds-blind")),
                    .. MountingHosts.Select(Sioc),
                ],
            },
        };

        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.Contains(result.Visible, a => a.Title == "Slow from every host");
        Assert.Contains(result.Visible, a => a.Title == "Storage latency cannot be measured here");

        // No rule threw on the way. A guarded rule that fails still reports,
        // so a green wiring test with this alert in the inbox would be proving
        // the guard rather than the wire.
        Assert.DoesNotContain(result.Visible, a => a.Title == "Analysis rule failed");
    }

    [Fact]
    public async Task The_path_redundancy_rule_is_reached_by_the_inventory_cycle()
    {
        // The only rule evaluated on the inventory rhythm, so the wiring block
        // the other six share does not cover it: dropping its call site would
        // leave every unit test in StoragePathRedundancyTests green and the
        // product permanently silent about a dead path. That is the exact
        // failure four earlier rules shipped with.
        //
        // It also pins the rule to this cycle rather than the metric one. The
        // path table is read on the inventory rhythm and reconciliation treats
        // what it is given as the whole truth, so a rule evaluated in the
        // wrong scope would have the faster cycle resolving its findings
        // seconds after they were raised.
        var cycle = Cycle();

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot(
                "vc-1", _clock.UtcNow, entities: [HostMissingAPath()]),
        };

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Contains(result.Visible, a => a.Title == "Storage path redundancy lost");

        // A guarded rule that throws still reports, so a green assertion above
        // with this alert present would be proving the guard rather than the
        // wire.
        Assert.DoesNotContain(result.Visible, a => a.Title == "Analysis rule failed");
    }

    /// <summary>
    /// A host reached over two paths, one of which the platform calls dead.
    /// </summary>
    [Fact]
    public async Task The_remote_logging_rule_is_reached_by_the_inventory_cycle()
    {
        // The second rule on the inventory rhythm, and the first that reads a
        // setting rather than a measurement. Dropping its call site would
        // leave every unit test in RemoteLoggingTests green while the product
        // never once mentions a host whose logs die with it.
        //
        // The fixture reports the setting as empty rather than omitting it,
        // because omitting it is the "never read" case the rule stays silent
        // about -- a wiring test built from that would pass whether or not
        // the rule is called.
        var cycle = Cycle();

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot(
                "vc-1", _clock.UtcNow, entities: [HostWithoutSyslog()]),
        };

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Contains(result.Visible, a => a.Title == "Host forwards no logs");

        // A guarded rule that throws still reports, so the assertion above
        // would be satisfied by the guard rather than the wire without this.
        Assert.DoesNotContain(result.Visible, a => a.Title == "Analysis rule failed");
    }

    [Fact]
    public async Task The_fill_date_rule_is_reached_by_the_inventory_cycle_and_reads_the_history()
    {
        // Roadmap M4.3. Dropping the registration would leave every test in
        // DatastoreTimeToFullTests green and the product silent about a volume
        // that fills on Friday. The history is recorded the way the cycle
        // records it -- as capacity readings -- and the cycle's own reading
        // supplies the capacity, so this passes only if the rule is reached,
        // is given this cycle's readings and reads the store.
        const double gb = 1024d * 1024 * 1024;
        var datastore = new EntityId("vc-1:datastore-41");

        _observations.Append(
        [
            .. Enumerable.Range(0, 20).Select(i => CapacityCounters.Reading(
                datastore, CapacityCounters.DatastoreUsed, (10 + 2 * i) * gb, T0.AddDays(i - 20), "vc-1")),
        ]);

        var cycle = Cycle();

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot(
                "vc-1",
                _clock.UtcNow,
                entities: [Node(datastore.Value, EntityKind.Datastore, "vmfs01")]) with
            {
                Observations =
                [
                    CapacityCounters.Reading(
                        datastore, CapacityCounters.DatastoreCapacity, 60 * gb, _clock.UtcNow, "vc-1"),
                    CapacityCounters.Reading(
                        datastore, CapacityCounters.DatastoreFree, 10 * gb, _clock.UtcNow, "vc-1"),
                    CapacityCounters.Reading(
                        datastore, CapacityCounters.DatastoreUsed, 50 * gb, _clock.UtcNow, "vc-1"),
                ],
            },
        };

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        // 2 GB a day, 10 GB left: five days, inside the critical week.
        var alert = Assert.Single(result.Visible, a => a.Title == DatastoreTimeToFull.FillingTitle);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(datastore, alert.Entity);
        Assert.Contains("days of history", alert.Description, StringComparison.Ordinal);

        Assert.DoesNotContain(result.Visible, a => a.Title == "Analysis rule failed");
    }

    /// <summary>
    /// A vCenter reporting one datastore filling at 2 GB a day with 10 GB
    /// left, over twenty days of recorded history in <paramref name="store"/>.
    /// </summary>
    private FakeInventorySource FillingDatastore(IObservationStore store, Func<bool> reachable)
    {
        const double gb = 1024d * 1024 * 1024;
        var datastore = new EntityId("vc-1:datastore-41");

        store.Append(
        [
            .. Enumerable.Range(0, 20).Select(i => CapacityCounters.Reading(
                datastore, CapacityCounters.DatastoreUsed, (10 + 2 * i) * gb, T0.AddDays(i - 20), "vc-1")),
        ]);

        return new FakeInventorySource("vc-1")
        {
            Behaviour = () => !reachable()
                ? throw new InvalidOperationException("unreachable")
                : Snapshot(
                    "vc-1",
                    _clock.UtcNow,
                    entities: [Node(datastore.Value, EntityKind.Datastore, "vmfs01")]) with
                {
                    Observations =
                    [
                        CapacityCounters.Reading(
                            datastore, CapacityCounters.DatastoreCapacity, 60 * gb, _clock.UtcNow, "vc-1"),
                        CapacityCounters.Reading(
                            datastore, CapacityCounters.DatastoreFree, 10 * gb, _clock.UtcNow, "vc-1"),
                        CapacityCounters.Reading(
                            datastore, CapacityCounters.DatastoreUsed, 50 * gb, _clock.UtcNow, "vc-1"),
                    ],
                },
        };
    }

    [Fact]
    public async Task A_vcenter_that_misses_an_inventory_read_keeps_its_datastores_alerts()
    {
        // The graph keeps a silent vCenter's datastores because we did not
        // look. The alerts on them used to resolve on the same cycle -- the
        // rule has no reading for them, so it says nothing -- and every fill
        // date on that vCenter closed after one missed read, to reopen and
        // notify again on the next.
        var reachable = true;
        var inventory = FillingDatastore(_observations, () => reachable);
        var cycle = Cycle();

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        var raised = Assert.Single(_alerts.All, a => a.Title == DatastoreTimeToFull.FillingTitle);
        Assert.Equal(AlertLifecycleState.Open, raised.State);

        reachable = false;
        _clock.Advance(TimeSpan.FromMinutes(5));
        var result = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Contains("vc-1", result.SilentSources);
        var held = Assert.Single(_alerts.All, a => a.Title == DatastoreTimeToFull.FillingTitle);
        Assert.Equal(AlertLifecycleState.Open, held.State);
        Assert.Equal(raised.LastSeenUtc, held.LastSeenUtc);

        // And once it answers without the problem, it resolves as before.
        reachable = true;
        _clock.Advance(TimeSpan.FromMinutes(5));
        var answered = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot(
                "vc-1", _clock.UtcNow, entities: [Node("vc-1:datastore-41", EntityKind.Datastore, "vmfs01")]),
        };
        await cycle.RunInventoryAsync([answered], Options, CancellationToken.None);

        Assert.Equal(
            AlertLifecycleState.Resolved,
            Assert.Single(_alerts.All, a => a.Title == DatastoreTimeToFull.FillingTitle).State);
    }

    [Fact]
    public async Task A_datastore_whose_history_cannot_be_read_keeps_its_alert_and_says_why()
    {
        var store = new FlakyObservationStore();
        var inventory = FillingDatastore(store, () => true);
        var cycle = Cycle(observations: store);

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        Assert.Equal(
            AlertLifecycleState.Open,
            Assert.Single(_alerts.All, a => a.Title == DatastoreTimeToFull.FillingTitle).State);

        store.QueryFails = true;
        _clock.Advance(TimeSpan.FromMinutes(5));
        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Equal(
            AlertLifecycleState.Open,
            Assert.Single(_alerts.All, a => a.Title == DatastoreTimeToFull.FillingTitle).State);
        Assert.Contains(_alerts.All, a => a.Title == DatastoreTimeToFull.HistoryUnreadableTitle);
        Assert.DoesNotContain(_alerts.All, a => a.Title == "Analysis rule failed");
    }

    [Fact]
    public async Task The_coverage_rule_is_reached_by_the_inventory_cycle()
    {
        // The rule that reports on the product rather than the estate, and
        // the one whose absence is hardest to notice: every other rule is
        // entitled to be silent, so dropping this call site leaves an estate
        // that cannot be read looking exactly like an estate with nothing
        // wrong. Its own unit tests would stay green throughout.
        var cycle = Cycle();

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow) with
            {
                Coverage =
                [
                    new PropertyCoverage
                    {
                        ObjectType = "HostSystem",
                        Property = "config.option",
                        Asked = 10,
                        Answered = 0,
                    },
                ],
            },
        };

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.Contains(
            result.Visible,
            a => a.Title == "A property this product reasons about could not be read");

        Assert.DoesNotContain(result.Visible, a => a.Title == "Analysis rule failed");
    }

    /// <summary>A host that answered about its log target, and said nothing.</summary>
    private Entity HostWithoutSyslog() => new()
    {
        Id = new EntityId("vc-1:host-9"),
        Kind = EntityKind.EsxiHost,
        DisplayName = "esx09",
        SourceInstanceId = "vc-1",
        LastSeenUtc = _clock.UtcNow,
        Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Syslog.global.logHost"] = string.Empty,
        },
    };

    /// <remarks>
    /// The shape the collector produces: a runtime name per path, an adapter,
    /// and the NAA that the second SCSI table resolved the device key into.
    /// Nothing about this host's counters changes when the path dies, which is
    /// the entire reason the rule exists.
    /// </remarks>
    private Entity HostMissingAPath() => new()
    {
        Id = new EntityId("vc-1:host-1"),
        Kind = EntityKind.EsxiHost,
        DisplayName = "esx01",
        SourceInstanceId = "vc-1",
        LastSeenUtc = _clock.UtcNow,
        StoragePaths =
        [
            new StoragePath
            {
                Name = "vmhba0:C0:T0:L1",
                State = "active",
                Adapter = "vmhba0",
                StorageDeviceId = "naa.600508b1001cb736",
                DeviceKey = "key-vim.host.ScsiDisk-0200",
            },
            new StoragePath
            {
                Name = "vmhba1:C0:T0:L1",
                State = "dead",
                Adapter = "vmhba1",
                StorageDeviceId = "naa.600508b1001cb736",
                DeviceKey = "key-vim.host.ScsiDisk-0200",
            },
        ],
    };

    [Fact]
    public async Task The_shared_volume_rule_is_given_the_peer_policy_the_peer_rule_got()
    {
        // The two rules are mutually exclusive by recomputing each other's
        // test, so they must read one set of numbers. The cycle forces this
        // rather than trusting both defaults to match: two copies that drifted
        // apart would open a band where both fire, or neither does, and
        // nothing would say so.
        //
        // Raising the peer floor above the readings must silence BOTH. If the
        // shared-volume rule kept its own default floor, it would still fire
        // here and this assertion would fail.
        var cycle = Cycle();

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations =
                [
                    .. MountingHosts.Select(h => Vantage(h, 9)),
                    .. MountingHosts.Select(h => Demand(h, 40)),
                ],
            },
        };

        var strict = Options with
        {
            PeerOutliers = PeerOutlierPolicy.Default with
            {
                MinimumMilliseconds = 50d,
            },
        };

        await cycle.RunObservationsAsync([metrics], strict, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var result = await cycle.RunObservationsAsync([metrics], strict, CancellationToken.None);

        Assert.DoesNotContain(result.Visible, a => a.Title == "Slow from every host");
        Assert.DoesNotContain(result.Visible, a => a.Title == "Slow from one host only");
    }

    /// <summary>What a volume is being asked to do, beside what it costs.</summary>
    /// <remarks>
    /// Both storage rules refuse to judge an idle volume: the average latency
    /// of a handful of requests is the fate of those requests, not a property
    /// of the storage.
    /// </remarks>
    private static Observation Demand(string host, double ops, string entity = "vc-1:ds-prod") => new()
    {
        Entity = new EntityId(entity),
        SampledAtUtc = T0,
        Source = "vc-1",
        Value = new CounterValue
        {
            CounterName = "datastore.numberReadAveraged.average",
            Raw = ops,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "number",
            Instance = host,
            InstanceIsVantagePoint = true,
        },
    };

    /// <summary>
    /// The counter collected solely as evidence that the latency numbers mean
    /// anything. Zero means Storage I/O Control is not running.
    /// </summary>
    private static Observation Sioc(string host) => new()
    {
        Entity = new EntityId("vc-1:ds-blind"),
        SampledAtUtc = T0,
        Source = "vc-1",
        Value = new CounterValue
        {
            CounterName = "datastore.siocActiveTimePercentage.average",
            Raw = 0,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "percent",
            Instance = host,
            InstanceIsVantagePoint = true,
        },
    };

    /// <summary>
    /// A fault reading that names no source, which FaultCounters cannot
    /// fingerprint.
    /// </summary>
    /// <remarks>
    /// Chosen over a hand-thrown exception because the rule is reached through
    /// the real cycle and falls over on its own code path --
    /// AlertFingerprint.Create rejecting a null source -- which is the shape a
    /// rule bug actually has. A fake rule wired in for the test would prove
    /// the test's own wiring rather than the product's.
    /// </remarks>
    private static Observation Sourceless() => new()
    {
        Entity = new EntityId("vc-1:host-1"),
        SampledAtUtc = T0,
        Source = null!,
        Value = new CounterValue
        {
            CounterName = "storagePath.busResets.summation",
            Raw = 1,
            Rollup = RollupType.Summation,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "number",
            Instance = "vmhba1:C0:T3:L7",
            IsFaultCount = true,
        },
    };

    /// <summary>
    /// One host's view of a shared volume, for the rule that must survive its
    /// sibling.
    /// </summary>
    /// <remarks>
    /// Milliseconds and a vantage-point instance, because PeerOutliers looks
    /// at nothing else. Three hosts is its minimum, and 12 against peers at
    /// zero clears both the floor and the ratio.
    /// </remarks>
    private static Observation Vantage(
        string host, double ms, string entity = "vc-1:ds-prod") => new()
    {
        Entity = new EntityId(entity),
        SampledAtUtc = T0,
        Source = "vc-1",
        Value = new CounterValue
        {
            CounterName = "datastore.totalReadLatency.average",
            Raw = ms,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "millisecond",
            Instance = host,
            InstanceIsVantagePoint = true,
        },
    };

    private static Observation Fault(string counter, double value, string instance) => new()
    {
        Entity = new EntityId("vc-1:host-1"),
        SampledAtUtc = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
        Source = "vc-1",
        Value = new CounterValue
        {
            CounterName = counter,
            Raw = value,
            Rollup = RollupType.Summation,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "number",
            Instance = instance,
            IsFaultCount = true,
        },
    };

    private static ObservationBatch Batch(string source, DateTimeOffset now) => new()
    {
        SourceInstanceId = source,
        ReadAtUtc = now,
    };

    private static Observation Reading(string counter, double value, DateTimeOffset at) => new()
    {
        Entity = new EntityId("vc-1:host-1"),
        SampledAtUtc = at,
        Source = "vc-1",
        Value = new CounterValue
        {
            CounterName = counter,
            Raw = value,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "percent",
        },
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

    [Fact]
    public async Task The_dropped_packet_rule_is_reached_by_the_metric_cycle()
    {
        // Dropping its call site would leave every DroppedPacketsTests case
        // green and the product silent about a guest losing five percent of
        // its frames -- the failure four earlier rules shipped with.
        var cycle = Cycle();

        Observation Packets(string counter, double raw) => new()
        {
            Entity = new EntityId("vc-1:vm-1"),
            SampledAtUtc = T0,
            Source = "vc-1",
            Value = new CounterValue
            {
                CounterName = counter,
                Raw = raw,
                Rollup = RollupType.Summation,
                Interval = TimeSpan.FromSeconds(20),
                Unit = "number",
            },
        };

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations =
                [
                    Packets("net.packetsRx.summation", 10_000),
                    Packets("net.droppedRx.summation", 500),
                ],
            },
        };

        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.Contains(result.Visible, a => a.Title == "Dropping received packets");
        Assert.DoesNotContain(result.Visible, a => a.Title == "Analysis rule failed");
    }

    [Fact]
    public async Task The_memory_pressure_rule_is_reached_and_is_given_the_graph_it_needs()
    {
        // The rule judges nothing the graph does not know the kind of, so a
        // call site that passed an empty graph -- or no call site at all --
        // would be silent here in exactly the same way. Inventory runs first
        // for that reason, and the machine is swapping on both cycles so the
        // warning survives hysteresis.
        var cycle = Cycle();

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot(
                "vc-1",
                _clock.UtcNow,
                entities: [Node("vc-1:vm-swap", EntityKind.VirtualMachine, "vm-swap")]),
        };

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations =
                [
                    new Observation
                    {
                        Entity = new EntityId("vc-1:vm-swap"),
                        SampledAtUtc = _clock.UtcNow,
                        Source = "vc-1",
                        Value = new CounterValue
                        {
                            CounterName = "mem.swapinRate.average",
                            Raw = 500,
                            Rollup = RollupType.Average,
                            Interval = TimeSpan.FromSeconds(20),
                            Unit = "kiloBytesPerSecond",
                        },
                    },
                ],
            },
        };

        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        Assert.Contains(result.Visible, a =>
            a.Title == "Memory is being swapped or compressed" &&
            a.Entity == new EntityId("vc-1:vm-swap"));
        Assert.DoesNotContain(result.Visible, a => a.Title == "Analysis rule failed");
    }

    [Fact]
    public async Task The_storage_noisy_neighbour_rule_is_given_the_graph_and_the_history_it_needs()
    {
        // The one rule that needs both the graph (which machines are stored on
        // the volume) and the sample store (whether the volume's load rose).
        // Wired without either it compiles, runs and finds nothing forever, so
        // this builds both: the inventory cycle first, and a day's worth of
        // ordinary load written into the store before the volume slows down.
        var cycle = Cycle();

        string[] guests = ["vc-1:vm-1", "vc-1:vm-2", "vc-1:vm-3", "vc-1:vm-4", "vc-1:vm-5"];

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot(
                "vc-1",
                _clock.UtcNow,
                entities:
                [
                    Node("vc-1:ds-prod", EntityKind.Datastore, "ds-prod"),
                    .. guests.Select(v => Node(v, EntityKind.VirtualMachine, v)),
                ],
                relationships:
                [
                    .. guests.Select(v => new Relationship
                    {
                        From = new EntityId(v),
                        To = new EntityId("vc-1:ds-prod"),
                        Kind = RelationshipKind.BackedBy,
                        ObservedAtUtc = _clock.UtcNow,
                    }),
                ]),
        };

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        // What the volume usually carries: 100 a host, five hours ago.
        _observations.Append(
        [
            .. MountingHosts.Select(h => Demand(h, 100) with { SampledAtUtc = T0.AddHours(-5) }),
        ]);

        static Observation Requests(string vm, double ops) => new()
        {
            Entity = new EntityId(vm),
            SampledAtUtc = T0,
            Source = "vc-1",
            Value = new CounterValue
            {
                CounterName = "virtualDisk.numberReadAveraged.average",
                Raw = ops,
                Rollup = RollupType.Average,
                Interval = TimeSpan.FromSeconds(20),
                Unit = "number",
            },
        };

        var metrics = new FakeObservationSource("vc-1")
        {
            Behaviour = () => Batch("vc-1", _clock.UtcNow) with
            {
                Observations =
                [
                    .. MountingHosts.Select(h => Vantage(h, 9)),
                    .. MountingHosts.Select(h => Demand(h, 400)),
                    Requests("vc-1:vm-1", 1000),
                    .. guests.Skip(1).Select(v => Requests(v, 50)),
                ],
            },
        };

        await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var result = await cycle.RunObservationsAsync([metrics], Options, CancellationToken.None);

        var alert = Assert.Single(
            result.Visible, a => a.Title == "Virtual machines are loading a slow volume");
        Assert.Contains("'vc-1:vm-1'", alert.Description, StringComparison.Ordinal);

        // A guarded rule that throws still reports, so the assertion above
        // could be satisfied by the guard rather than the wire without this.
        Assert.DoesNotContain(result.Visible, a => a.Title == "Analysis rule failed");
    }

    [Fact]
    public async Task A_stored_vcenter_event_opens_an_alert_once_and_its_clear_resolves_it()
    {
        // The event rule reads the event store, not the snapshot. Wired
        // without the store it compiles, runs and finds nothing forever; wired
        // to read only new events it would raise once and be resolved on the
        // very next cycle. So: two cycles on the same stored event must keep
        // one alert and notify once, and the restoration must close it.
        var events = new InMemoryEventStore();
        var cycle = new MonitoringCycle(
            new InventoryCollectionPipeline(_clock),
            new ObservationCollectionPipeline(_clock),
            _graphs,
            _alerts,
            _health,
            _coverage,
            _notifier,
            _observations,
            _maintenance,
            _clock,
            events);

        SourceEvent Storage(long key, string type, string message) => new()
        {
            Key = key,
            CreatedAtUtc = _clock.UtcNow.AddMinutes(-1),
            EventClass = "EventEx",
            TypeId = type,
            Severity = "error",
            Message = message,
            Host = new EventObjectRef { MoRef = "host-1", Name = "esx01" },
        };

        events.Record(
            "vc-1",
            [Storage(1, "esx.problem.storage.connectivity.lost",
                "Lost connectivity to storage device naa.600a0b80. Path vmhba64:C4:T0:L0 is down.")],
            complete: true,
            _clock.UtcNow);

        var inventory = new FakeInventorySource("vc-1")
        {
            Behaviour = () => Snapshot("vc-1", _clock.UtcNow),
        };

        await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        var alert = Assert.Single(second.Visible, a => a.Title == "Storage device connectivity lost");
        Assert.Equal(new EntityId("vc-1:host-1"), alert.Entity);
        Assert.Single(_notifier.Dispatched, a => a.Title == "Storage device connectivity lost");
        Assert.DoesNotContain(second.Visible, a => a.Title == "Analysis rule failed");

        events.Record(
            "vc-1",
            [Storage(2, "esx.clear.storage.connectivity.restored",
                "Connectivity to storage device naa.600a0b80 (Datastores: 'ds1') restored. Path vmhba64:C4:T0:L0 is active again.")],
            complete: true,
            _clock.UtcNow);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var third = await cycle.RunInventoryAsync([inventory], Options, CancellationToken.None);

        Assert.DoesNotContain(third.Visible, a => a.Title == "Storage device connectivity lost");
    }
}

/// <summary>
/// The real fake, with a note taken of what reconciliation produced.
/// </summary>
/// <remarks>
/// A decorator rather than a second implementation: everything is passed
/// through to <see cref="InMemoryAlertStateStore"/>, so the cycle under test
/// behaves exactly as it does everywhere else in this file. What it records is
/// the result as the reconciler produced it — before the store filed it, and so
/// before any stamping a store does on the way in.
/// </remarks>
internal sealed class ScopeWatchingAlertStateStore(InMemoryAlertStateStore inner) : IAlertStateStore
{
    private readonly List<AlertInstance> _reconciled = [];

    public IReadOnlyList<AlertInstance> Reconciled => _reconciled;

    public IReadOnlyList<AlertInstance> All => inner.All;

    public IReadOnlyList<AlertInstance> InstancesIn(string scope) => inner.InstancesIn(scope);

    public IReadOnlyList<FlapHistory> FlapHistoriesIn(string scope) => inner.FlapHistoriesIn(scope);

    public AlertReconciliationResult Reconcile(
        string scope,
        Func<IReadOnlyList<AlertInstance>, IReadOnlyList<FlapHistory>, AlertReconciliationResult> reconcile)
    {
        var result = inner.Reconcile(scope, reconcile);

        _reconciled.AddRange(result.Instances);

        return result;
    }

    public AlertInstance? Mutate(AlertFingerprint fingerprint, Func<AlertInstance, AlertInstance> change) =>
        inner.Mutate(fingerprint, change);

    public IReadOnlyList<AlertInstance> MutateMany(
        IReadOnlyList<AlertFingerprint> fingerprints, Func<AlertInstance, AlertInstance> change) =>
        inner.MutateMany(fingerprints, change);

    public void MarkNotified(string scope, IReadOnlyList<AlertFingerprint> fingerprints) =>
        inner.MarkNotified(scope, fingerprints);
}

/// <summary>A sample store whose failure can end, as a real one's does.</summary>
/// <remarks>
/// FailingObservationStore fails forever, which is enough to prove the cycle
/// survives a failure and cannot prove anything about what happens afterwards.
/// The defect these cover lives entirely on the far side of the recovery: a
/// database restarts, one write is lost, and the product keeps reporting that
/// loss long after writing works again.
/// </remarks>
internal sealed class FlakyObservationStore : IObservationStore
{
    private readonly InMemoryObservationStore _kept = new();

    public bool Fails { get; set; }

    public void Append(IReadOnlyList<Observation> observations)
    {
        if (Fails)
        {
            throw new IOException("the connection was reset by the server");
        }

        _kept.Append(observations);
    }

    /// <summary>Whether reading history fails, as a statement timeout does.</summary>
    public bool QueryFails { get; set; }

    public SeriesResult Query(SeriesQuery query) => QueryFails
        ? throw new TimeoutException("canceling statement due to statement timeout")
        : _kept.Query(query);

    public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => _kept.SeriesFor(entity);

    public CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy) =>
        _kept.Compact(nowUtc, policy);
}

/// <remarks>
/// Throws the way a busy database does — not at the read, which would look
/// like an unreachable collector, but at the write, once every source has
/// already answered and this cycle's work is sitting in memory.
/// </remarks>
internal sealed class FailingCollectorHealthStore : ICollectorHealthStore
{
    public IReadOnlyList<CollectorHealth> Current => [];

    public void Merge(IReadOnlyList<CollectorHealth> health) =>
        throw new TimeoutException("the connection pool is exhausted");
}

internal sealed class FailingEntityGraphStore : IEntityGraphStore
{
    public EntityGraph Current => new();

    public void Replace(EntityGraph graph) =>
        throw new TimeoutException("canceling statement due to statement timeout");
}
