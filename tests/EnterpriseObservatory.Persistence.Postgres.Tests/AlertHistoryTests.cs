using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// The durable alert history (migration 14) and the three-valued state of
/// ADR-0026, against the real tables.
/// </summary>
public sealed class AlertHistoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Raw = TimeSpan.FromDays(2);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private static readonly EvidenceSources VcOne = new()
    {
        Reporting = ["vc-1"],
        OwnerOf = _ => "vc-1",
    };

    private static AlertDefinition Fault(string name, AlertSeverity severity = AlertSeverity.Critical) => new()
    {
        Fingerprint = AlertFingerprint.Create("vc-1", name, "Fault", $"esx01/{name}", "fault-counter"),
        Severity = severity,
        Title = name,
        Category = "Fault",
        Source = "vc-1",
        Entity = new EntityId("vc-1:host-1"),
    };

    private static ConditionPresent Present(AlertDefinition alert, DateTimeOffset at) => new()
    {
        Covers = [alert.Fingerprint],
        Alerts = [alert],
        Entity = alert.Entity,
        EvidenceAtUtc = at,
    };

    private static ConditionAbsent Absent(AlertDefinition alert, DateTimeOffset at) => new()
    {
        Covers = [alert.Fingerprint],
        Entity = alert.Entity,
        EvidenceAtUtc = at,
    };

    private static AlertReconciliationResult Cycle(
        PostgresAlertStateStore store,
        DateTimeOffset now,
        IReadOnlyList<SubjectVerdict> verdicts,
        string rule = FaultCounters.RuleId,
        string scope = AlertScopes.Observation,
        int n = 1) =>
        store.Reconcile(scope, (stored, flaps) => AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Scope = scope,
            Stored = stored,
            FlapHistories = flaps,
            NowUtc = now,
            Evaluations = [new RuleEvaluation(rule, new ResolutionPolicy { ConsecutiveAbsent = n }, verdicts)],
            Sources = VcOne,
            RawRetention = Raw,
        }));

    [SkippableFact]
    public void The_three_valued_fields_survive_a_restart()
    {
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var psu = Fault("psu");

        Cycle(store, T0, [Present(psu, T0)], n: 3);
        Cycle(store, T0.AddSeconds(30), [Absent(psu, T0.AddSeconds(30))], n: 3);

        // Then the rule says nothing: stale, "not reported".
        Cycle(store, T0.AddSeconds(60), [], n: 3);

        _live.Restart();

        var recovered = Assert.Single(new PostgresAlertStateStore(_live.Database).All);

        Assert.Equal(FaultCounters.RuleId, recovered.RuleId);
        Assert.Equal(T0.AddSeconds(30), recovered.EvidenceAtUtc);
        Assert.Equal(T0.AddSeconds(60), recovered.StaleSinceUtc);
        Assert.Equal(UnknownReason.NotReported, recovered.StaleReason);
        Assert.Contains("gave no verdict", recovered.StaleDetail, StringComparison.Ordinal);
        Assert.Equal(0, recovered.ConsecutiveAbsent);

        // Going stale is not a transition, so it is not a history row: the
        // reason and detail above are on the instance, and the history is
        // only the opening.
        Assert.Equal(["Confirmed"], Reasons(psu.Fingerprint, T0));
    }

    [SkippableFact]
    public void The_count_towards_resolution_survives_a_restart()
    {
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var psu = Fault("psu");

        Cycle(store, T0, [Present(psu, T0)], n: 3);
        Cycle(store, T0.AddSeconds(30), [Absent(psu, T0.AddSeconds(30))], n: 3);
        Cycle(store, T0.AddSeconds(60), [Absent(psu, T0.AddSeconds(60))], n: 3);

        _live.Restart();
        store = new PostgresAlertStateStore(_live.Database);
        Assert.Equal(2, Assert.Single(store.All).ConsecutiveAbsent);

        Cycle(store, T0.AddSeconds(90), [Absent(psu, T0.AddSeconds(90))], n: 3);

        Assert.Equal(AlertLifecycleState.Resolved, Assert.Single(store.All).State);
    }

    [SkippableFact]
    public void An_alert_in_the_unknown_state_survives_a_restart_and_comes_back_to_where_it_was()
    {
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var psu = Fault("psu");

        Cycle(store, T0, [Present(psu, T0)]);
        store.Mutate(psu.Fingerprint, i => AlertLifecycle.Acknowledge(i, "op", T0.AddMinutes(1)));
        Cycle(store, T0.AddMinutes(2), []);
        Cycle(store, T0 + Raw, []);

        _live.Restart();
        store = new PostgresAlertStateStore(_live.Database);
        Assert.Equal(AlertLifecycleState.Unknown, Assert.Single(store.All).State);

        Cycle(store, T0 + Raw + TimeSpan.FromMinutes(1), [Present(psu, T0 + Raw + TimeSpan.FromMinutes(1))]);

        Assert.Equal(AlertLifecycleState.Acknowledged, Assert.Single(store.All).State);
    }

    [SkippableFact]
    public void A_retired_alerts_history_stays_and_a_new_episode_starts_its_own()
    {
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var psu = Fault("psu");

        Cycle(store, T0, [Present(psu, T0)]);
        Cycle(store, T0.AddSeconds(30), [Absent(psu, T0.AddSeconds(30))]);
        Cycle(store, T0.AddSeconds(60), [Absent(psu, T0.AddSeconds(60))]);
        Assert.Empty(store.All);

        Assert.Equal(["Confirmed", "ConditionCleared"], Reasons(psu.Fingerprint, T0));

        // The same fault again, later: a second life of the fingerprint,
        // recorded beside the first rather than over it.
        var later = T0.AddHours(1);
        Cycle(store, later, [Present(psu, later)]);

        Assert.Equal(["Confirmed", "ConditionCleared"], Reasons(psu.Fingerprint, T0));
        Assert.Equal(["Confirmed"], Reasons(psu.Fingerprint, later));

        _live.Restart();

        var reborn = Assert.Single(new PostgresAlertStateStore(_live.Database).All);
        Assert.Equal(AlertTransitionReason.Confirmed, Assert.Single(reborn.History).Reason);
    }

    [SkippableFact]
    public void Writing_the_same_instance_again_does_not_duplicate_its_history()
    {
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var psu = Fault("psu");

        for (var i = 0; i < 5; i++)
        {
            Cycle(store, T0.AddSeconds(30 * i), [Present(psu, T0.AddSeconds(30 * i))]);
        }

        store.Mutate(psu.Fingerprint, i => AlertLifecycle.Acknowledge(i, "op", T0.AddMinutes(5)));
        store.MarkNotified(AlertScopes.Observation, [psu.Fingerprint]);

        Assert.Equal(["Confirmed", "OperatorAcknowledged"], Reasons(psu.Fingerprint, T0));
    }

    [SkippableFact]
    public void A_resolved_alert_is_found_in_its_window_after_it_has_been_retired()
    {
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var psu = Fault("psu");
        var fan = Fault("fan");

        Cycle(store, T0, [Present(psu, T0), Present(fan, T0)]);
        store.Mutate(psu.Fingerprint, i => AlertLifecycle.Acknowledge(i, "ertugrul", T0.AddSeconds(10)));
        Cycle(store, T0.AddSeconds(30), [Absent(psu, T0.AddSeconds(30)), Present(fan, T0.AddSeconds(30))]);
        Cycle(store, T0.AddSeconds(60), [Absent(psu, T0.AddSeconds(60)), Present(fan, T0.AddSeconds(60))]);

        _live.Restart();
        store = new PostgresAlertStateStore(_live.Database);

        var resolved = Assert.Single(store.ResolvedBetween(T0, T0.AddMinutes(1)));

        Assert.Equal("psu", resolved.Title);
        Assert.Equal(AlertLifecycleState.Resolved, resolved.State);
        Assert.Equal(AlertSeverity.Critical, resolved.Severity);
        Assert.Equal(T0, resolved.FirstSeenUtc);
        Assert.Equal(FaultCounters.RuleId, resolved.RuleId);
        Assert.Contains(resolved.History, t => t.Reason == AlertTransitionReason.OperatorAcknowledged && t.Actor == "ertugrul");
        Assert.Equal(T0.AddSeconds(30), resolved.History[^1].AtUtc);

        Assert.Empty(store.ResolvedBetween(T0.AddMinutes(2), T0.AddMinutes(3)));
    }

    [SkippableFact]
    public void An_alert_that_resolved_and_came_back_is_not_listed_as_resolved()
    {
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var psu = Fault("psu");

        Cycle(store, T0, [Present(psu, T0)]);
        Cycle(store, T0.AddSeconds(30), [Absent(psu, T0.AddSeconds(30))]);
        Cycle(store, T0.AddSeconds(60), [Present(psu, T0.AddSeconds(60))]);

        Assert.Empty(store.ResolvedBetween(T0, T0.AddMinutes(5)));
    }

    [SkippableFact]
    public void Pruning_removes_ended_episodes_past_retention_and_never_a_live_one()
    {
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var gone = Fault("gone");
        var live = Fault("live");

        Cycle(store, T0, [Present(gone, T0), Present(live, T0)]);
        Cycle(store, T0.AddSeconds(30), [Absent(gone, T0.AddSeconds(30)), Present(live, T0.AddSeconds(30))]);
        Cycle(store, T0.AddSeconds(60), [Absent(gone, T0.AddSeconds(60)), Present(live, T0.AddSeconds(60))]);

        var removed = store.PruneHistory(T0.AddDays(1));

        Assert.Equal(2, removed);
        Assert.Empty(Reasons(gone.Fingerprint, T0));
        Assert.Equal(["Confirmed"], Reasons(live.Fingerprint, T0));
        Assert.Equal(AlertTransitionReason.Confirmed, Assert.Single(Assert.Single(store.All).History).Reason);
    }

    // --- only real transitions are history (post-#83 measurement) -------------

    private static Observation Datastore(string host, string counter, double raw, string unit, DateTimeOffset at) => new()
    {
        Entity = new EntityId("vc-1:ds-prod"),
        Source = "vc-1",
        SampledAtUtc = at,
        Value = new CounterValue
        {
            CounterName = counter,
            Raw = raw,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = unit,
            Instance = host,
            InstanceIsVantagePoint = true,
        },
    };

    /// <summary>A volume whose latency reads zero on three hosts, SIOC idle, at the given load.</summary>
    private static List<Observation> BlindSpot(double load, DateTimeOffset at) =>
    [
        Datastore("esx01", "datastore.totalReadLatency.average", 0, "millisecond", at),
        Datastore("esx02", "datastore.totalReadLatency.average", 0, "millisecond", at),
        Datastore("esx03", "datastore.totalReadLatency.average", 0, "millisecond", at),
        Datastore("esx01", "datastore.numberReadAveraged.average", load, "number", at),
        Datastore("esx01", "datastore.siocActiveTimePercentage.average", 0, "percent", at),
    ];

    private long HistoryRows(string? reason = null) => _live.Database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = reason is null
            ? "SELECT count(*) FROM alert_history;"
            : "SELECT count(*) FROM alert_history WHERE reason = @reason;";
        command.Parameters.AddWithValue("@reason", reason ?? string.Empty);
        return (long)command.ExecuteScalar()!;
    });

    [SkippableFact]
    public void A_blind_spot_going_busy_and_quiet_writes_one_raised_row_and_nothing_on_a_quiet_cycle()
    {
        // Measured after #83: 307 Raised rows in 43 minutes for the same
        // fingerprints. A quiet cycle made the rule call the volume absent,
        // the unconfirmed alert was forgotten, and the next busy cycle raised
        // it again as a new episode with a new "Raised" row. Now a quiet
        // cycle is Unknown(NotJudgeable), a pending alert survives it, and
        // only a real transition is written.
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var rule = new StorageLatencyBlindSpotRule();
        bool[] busy = [true, false, true, false, true, true, false, false, false, true, false, true];

        for (var i = 0; i < busy.Length; i++)
        {
            var now = T0.AddSeconds(30 * i);
            var verdicts = rule.Evaluate(new RuleContext
            {
                Observations = BlindSpot(busy[i] ? 1638 : 0.2, now),
                ReadGraph = () => EntityGraph.Empty,
                NowUtc = now,
                Options = new MonitoringOptions(),
                Series = new PostgresObservationStore(_live.Database),
                Events = new PostgresEventStore(_live.Database),
            });

            if (!busy[i])
            {
                Assert.Equal(UnknownReason.NotJudgeable, Assert.IsType<Unknown>(Assert.Single(verdicts)).Reason);
            }

            var rows = HistoryRows();
            Cycle(store, now, verdicts, rule.RuleId, n: rule.Resolution.ConsecutiveAbsent);

            if (!busy[i])
            {
                Assert.Equal(rows, HistoryRows());
            }
        }

        var alert = Assert.Single(store.All);
        Assert.Equal(AlertLifecycleState.Open, alert.State);
        Assert.Equal(1, HistoryRows(nameof(AlertTransitionReason.Raised)));
        Assert.Equal(1, HistoryRows());
    }

    [SkippableFact]
    public void An_alert_that_is_already_open_or_pending_never_gets_a_second_raised_row()
    {
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var fan = Fault("fan", AlertSeverity.Warning);

        // Pending, then confirmed, then seen for a while: one opening row.
        for (var i = 0; i < 6; i++)
        {
            Cycle(store, T0.AddSeconds(30 * i), [Present(fan, T0.AddSeconds(30 * i))], n: 3);
        }

        Assert.Equal(["Raised"], Reasons(fan.Fingerprint, T0));
    }

    [SkippableFact]
    public void An_unconfirmed_alert_that_comes_and_goes_leaves_no_history()
    {
        // Never shown to anyone, so never recorded: the flap tables keep the
        // fact that it was unstable, the durable history keeps what an
        // operator could have seen.
        RequireDatabase();

        var store = new PostgresAlertStateStore(_live.Database);
        var fan = Fault("fan", AlertSeverity.Warning);

        for (var i = 0; i < 6; i++)
        {
            var now = T0.AddSeconds(30 * i);
            Cycle(store, now, [i % 2 == 0 ? Present(fan, now) : Absent(fan, now)]);
        }

        Assert.Equal(0, HistoryRows());
    }

    // --- the restart contract (design note §2, §5) ---------------------------

    [SkippableFact]
    public async Task A_restart_followed_by_an_empty_cycle_resolves_nothing_and_notifies_nothing()
    {
        RequireDatabase();

        // Stored open alarms of both scopes, notified long ago.
        var store = new PostgresAlertStateStore(_live.Database);
        var psu = Fault("psu");
        var fan = Fault("fan");
        var syslog = Fault("syslog") with
        {
            Fingerprint = AlertFingerprint.Create("platform", "no remote syslog", "Configuration", "esx01", "remote-logging"),
        };

        Cycle(store, T0, [Present(psu, T0), Present(fan, T0)]);
        Cycle(store, T0, [Present(syslog, T0)], RemoteLogging.RuleId, AlertScopes.Inventory);
        store.MarkNotified(AlertScopes.Observation, [psu.Fingerprint, fan.Fingerprint]);
        store.MarkNotified(AlertScopes.Inventory, [syslog.Fingerprint]);

        Assert.Equal(3, store.All.Count(a => a.IsVisible));
        var resolvedBefore = ResolvedRows();

        // The service restarts; the first cycle of each kind has no
        // observations and no source that reported.
        _live.Restart();

        var clock = new FixedClock(T0.AddMinutes(10));
        var notifier = new CountingNotifier();
        var alerts = new PostgresAlertStateStore(_live.Database);

        var cycle = new MonitoringCycle(
            new InventoryCollectionPipeline(clock),
            new ObservationCollectionPipeline(clock),
            new PostgresEntityGraphStore(_live.Database),
            alerts,
            new PostgresCollectorHealthStore(_live.Database),
            new PostgresCoverageStore(_live.Database),
            notifier,
            new PostgresObservationStore(_live.Database),
            new PostgresMaintenanceWindowStore(_live.Database),
            clock,
            new PostgresEventStore(_live.Database));

        await cycle.RunObservationsAsync([], new MonitoringOptions(), CancellationToken.None);
        await cycle.RunInventoryAsync([], new MonitoringOptions(), CancellationToken.None);

        // Counted against the durable history, not the cache.
        Assert.Equal(resolvedBefore, ResolvedRows());
        Assert.Equal(0, notifier.Dispatched);
        Assert.Equal(3, alerts.All.Count(a => a.IsVisible));
        Assert.All(alerts.All, a => Assert.True(a.IsStale));
    }

    // --- reading the tables -------------------------------------------------

    private List<string> Reasons(AlertFingerprint fingerprint, DateTimeOffset episode) =>
        _live.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT reason FROM alert_history
                WHERE fingerprint = @fingerprint AND episode_first_seen_utc = @episode
                ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("@fingerprint", fingerprint.Value);
            command.Parameters.AddWithValue("@episode", episode);

            using var reader = command.ExecuteReader();
            var reasons = new List<string>();

            while (reader.Read())
            {
                reasons.Add(reader.GetString(0));
            }

            return reasons;
        });

    private long ResolvedRows() => _live.Database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM alert_history WHERE to_state = 'Resolved';";
        return (long)command.ExecuteScalar()!;
    });

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class CountingNotifier : IAlertNotifier
    {
        public int Dispatched { get; private set; }

        public Task DispatchAsync(IReadOnlyList<AlertInstance> pending, CancellationToken cancellationToken)
        {
            Dispatched += pending.Count;
            return Task.CompletedTask;
        }
    }
}
