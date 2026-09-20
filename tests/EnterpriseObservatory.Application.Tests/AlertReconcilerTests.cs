using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

public class AlertReconcilerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Cycle(int n) => T0.AddSeconds(30 * n);

    private static AlertDefinition Psu(AlertSeverity severity = AlertSeverity.Critical) => new()
    {
        Fingerprint = AlertFingerprint.Create("ilo", "PSU 2 failed", "Hardware", "esx01"),
        Severity = severity,
        Title = "PSU 2 failed",
        Entity = new EntityId("esx01"),
    };

    private static AlertDefinition Fan() => new()
    {
        Fingerprint = AlertFingerprint.Create("ilo", "Fan 3 degraded", "Hardware", "esx01"),
        Severity = AlertSeverity.Warning,
        Title = "Fan 3 degraded",
        Entity = new EntityId("esx01"),
    };

    private static AlertReconciliationResult Run(
        IReadOnlyList<AlertDefinition> observed,
        AlertReconciliationResult? previous = null,
        DateTimeOffset? now = null,
        FlapPolicy? flap = null,
        string scope = AlertScopes.Observation) =>
        AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Scope = scope,
            Observed = observed,
            Stored = previous?.Instances ?? [],
            FlapHistories = previous?.FlapHistories ?? [],
            Flap = flap ?? FlapPolicy.Default,
            NowUtc = now ?? Cycle(0),
        });

    [Fact]
    public void A_new_critical_is_visible_and_owed_a_notification_immediately()
    {
        var result = Run([Psu()]);

        Assert.Single(result.Visible);
        Assert.Single(result.ToNotify);
        Assert.Equal(AlertNotificationKind.Raised, result.ToNotify[0].PendingNotification);
    }

    [Fact]
    public void Info_alerts_are_dropped_before_they_reach_the_lifecycle()
    {
        // Dropped centrally so that a new alert source cannot forget to.
        var info = Psu() with { Severity = AlertSeverity.Info };

        var result = Run([info]);

        Assert.Empty(result.Instances);
    }

    [Fact]
    public void The_same_problem_reported_by_two_sources_is_one_alert()
    {
        var fromIlo = Psu(AlertSeverity.Warning);
        var fromOneView = Psu(AlertSeverity.Critical);

        var result = Run([fromIlo, fromOneView]);

        var instance = Assert.Single(result.Instances);
        // The worse reading wins, rather than whichever collector happened to
        // run last.
        Assert.Equal(AlertSeverity.Critical, instance.Severity);
    }

    [Fact]
    public void A_problem_that_stops_being_reported_resolves()
    {
        var first = Run([Psu()], now: Cycle(0));

        var second = Run([], previous: first, now: Cycle(1));

        Assert.Empty(second.Visible);
        var instance = Assert.Single(second.Instances);
        Assert.Equal(AlertLifecycleState.Resolved, instance.State);
    }

    [Fact]
    public void A_resolved_problem_that_stays_away_is_retired_from_storage()
    {
        var r = Run([Psu()], now: Cycle(0));
        r = Run([], previous: r, now: Cycle(1));
        r = Run([], previous: r, now: Cycle(2));

        Assert.Empty(r.Instances);
        Assert.Single(r.Retired);
    }

    [Fact]
    public void Independent_problems_are_tracked_independently()
    {
        // Two cycles so the warning clears hysteresis; a fan seen once and gone
        // would be forgotten rather than resolved, which is a different test.
        var state = Run([Psu(), Fan()], now: Cycle(0));
        state = Run([Psu(), Fan()], previous: state, now: Cycle(1));
        Assert.Equal(2, state.Instances.Count);

        // The fan recovers; the PSU does not.
        state = Run([Psu()], previous: state, now: Cycle(2));

        var psu = state.Instances.Single(i => i.Title == "PSU 2 failed");
        var fan = state.Instances.Single(i => i.Title == "Fan 3 degraded");

        Assert.Equal(AlertLifecycleState.Open, psu.State);
        Assert.Equal(AlertLifecycleState.Resolved, fan.State);
    }

    [Fact]
    public void A_flapping_alert_resolves_by_itself_once_the_signal_settles()
    {
        // Re-derived every cycle, so when the transitions age out of the window
        // it simply stops being observed and resolves like anything else.
        var flap = new FlapPolicy { Threshold = 3, Window = TimeSpan.FromMinutes(5) };

        AlertReconciliationResult? state = null;
        for (var cycle = 0; cycle < 8; cycle++)
        {
            var observed = cycle % 2 == 0 ? new[] { Fan() } : [];
            state = Run(observed, previous: state, now: Cycle(cycle), flap: flap);
        }

        Assert.NotNull(state);
        Assert.Contains(state.Instances, i => i.Title == "Unstable signal");

        // An hour of quiet: nothing observed, nothing ceasing, window empties.
        state = Run([], previous: state, now: Cycle(8).AddHours(1), flap: flap);
        state = Run([], previous: state, now: Cycle(8).AddHours(1).AddSeconds(30), flap: flap);

        var flapping = state.Instances.SingleOrDefault(i => i.Title == "Unstable signal");
        Assert.True(flapping is null || flapping.State == AlertLifecycleState.Resolved);
    }

    [Fact]
    public void A_persisting_problem_is_notified_once_not_every_cycle()
    {
        var r = Run([Psu()], now: Cycle(0));
        Assert.Single(r.ToNotify);

        // The dispatcher records what it sent.
        r = r with { Instances = [.. r.Instances.Select(AlertLifecycle.MarkNotified)] };

        r = Run([Psu()], previous: r, now: Cycle(1));
        r = Run([Psu()], previous: r, now: Cycle(2));

        Assert.Empty(r.ToNotify);
        Assert.Single(r.Visible);
    }

    [Fact]
    public void An_unstable_signal_eventually_raises_an_alert_of_its_own()
    {
        // The whole point: each individual appearance is an unconfirmed warning
        // that vanishes and is forgotten, so without flap detection the
        // instability would leave no trace at all.
        var flap = new FlapPolicy { Threshold = 3, Window = TimeSpan.FromHours(1) };

        AlertReconciliationResult? state = null;
        for (var cycle = 0; cycle < 10; cycle++)
        {
            var observed = cycle % 2 == 0 ? new[] { Fan() } : [];
            state = Run(observed, previous: state, now: Cycle(cycle), flap: flap);
        }

        Assert.NotNull(state);
        var flapping = state.Instances.SingleOrDefault(i => i.Title == "Unstable signal");

        Assert.NotNull(flapping);
        Assert.Equal(AlertSeverity.Warning, flapping.Severity);
    }

    [Fact]
    public void The_flapping_alert_does_not_replace_the_underlying_problem()
    {
        // "The fan is degraded" and "this fan reading will not hold still" are
        // different problems with different actions.
        var flap = new FlapPolicy { Threshold = 3, Window = TimeSpan.FromHours(1) };

        AlertReconciliationResult? state = null;
        for (var cycle = 0; cycle < 10; cycle++)
        {
            var observed = cycle % 2 == 0 ? new[] { Fan() } : [];
            state = Run(observed, previous: state, now: Cycle(cycle), flap: flap);
        }

        Assert.NotNull(state);
        // Feed it once more, steadily, so the underlying warning can confirm.
        state = Run([Fan()], previous: state, now: Cycle(10), flap: flap);
        state = Run([Fan()], previous: state, now: Cycle(11), flap: flap);

        Assert.Contains(state.Instances, i => i.Title == "Fan 3 degraded");
        Assert.Contains(state.Instances, i => i.Title == "Unstable signal");
    }

    [Fact]
    public void Maintenance_suppresses_the_notification_but_not_the_alert()
    {
        var window = new MaintenanceWindow
        {
            Id = "mw-1",
            Title = "Firmware upgrade",
            StartUtc = Cycle(0),
            EndUtc = Cycle(100),
            Entities = [new EntityId("esx01")],
        };

        var result = AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Scope = AlertScopes.Observation,
            Observed = [Psu()],
            MaintenanceWindows = [window],
            NowUtc = Cycle(1),
        });

        Assert.Single(result.Visible);
        Assert.Empty(result.ToNotify);
        Assert.Equal("mw-1", result.Visible[0].SuppressedByWindowId);
    }

    // --- scope ------------------------------------------------------------

    [Fact]
    public void An_alert_observed_without_a_scope_is_filed_under_the_cycle_that_reconciled_it()
    {
        // The defect this design removes. The analysis rules and GuardedRule
        // produce definitions with no scope -- they have no reason to know
        // which cycle is running them -- and AlertLifecycle copies what it is
        // given. Every fault-counter and peer-outlier alert therefore sat in
        // the live cache under "" while the database row beside it said
        // "observation", so the same alert answered differently before and
        // after a restart. If this assertion fails, an operator asking which
        // evaluation owns an alert gets an answer that depends on process
        // uptime, and the alert is filed under a slice no cycle reconciles --
        // meaning it never resolves and never appears in an inbox.
        var result = Run([Psu() with { Scope = string.Empty }], scope: AlertScopes.Inventory);

        Assert.Equal(AlertScopes.Inventory, Assert.Single(result.Instances).Scope);
    }

    [Fact]
    public void A_definition_claiming_the_wrong_scope_does_not_get_to_keep_it()
    {
        // Stamping rather than trusting, and the difference matters because the
        // pipelines still stamp their own definitions: the inventory pipeline
        // marks what its collectors report, and if one of those definitions
        // ever reached the metric cycle it would otherwise be filed under
        // inventory while the store wrote it into the observation slice. The
        // two would then disagree about where it lives, and the inventory cycle
        // would resolve an alert it had never been given -- on the next pass,
        // silently.
        var result = Run([Psu() with { Scope = AlertScopes.Inventory }], scope: AlertScopes.Observation);

        Assert.Equal(AlertScopes.Observation, Assert.Single(result.Instances).Scope);
    }

    [Fact]
    public void A_flap_history_and_the_alert_derived_from_it_land_in_the_reconciled_scope()
    {
        // Flap histories outlive the instances they describe and are the one
        // thing here that is built rather than observed, so they were a third
        // path to the same field. The derived alert takes its scope from the
        // history; a history in the wrong scope raises "Unstable signal" into
        // an evaluation that never observes it again, so the next pass of that
        // cycle resolves it -- the instability alert would appear and vanish
        // every other cycle, which is precisely the behaviour flap detection
        // exists to report about something else.
        var flap = new FlapPolicy { Threshold = 3, Window = TimeSpan.FromHours(1) };

        AlertReconciliationResult? state = null;
        for (var cycle = 0; cycle < 10; cycle++)
        {
            var observed = cycle % 2 == 0 ? new[] { Fan() } : [];
            state = Run(observed, previous: state, now: Cycle(cycle), flap: flap, scope: AlertScopes.Inventory);
        }

        Assert.NotNull(state);
        Assert.All(state.FlapHistories, f => Assert.Equal(AlertScopes.Inventory, f.Scope));
        Assert.Equal(
            AlertScopes.Inventory,
            Assert.Single(state.Instances, i => i.Title == "Unstable signal").Scope);
    }

    [Fact]
    public void A_reconciliation_without_a_scope_is_refused_rather_than_filed_under_nothing()
    {
        // `required` stops a caller forgetting the field; nothing stops a
        // caller passing a scope it never worked out. An empty one is not a
        // harmless default: the store would file real alerts under a slice no
        // cycle ever reconciles, so they would be invisible in every inbox,
        // never resolve, and never notify. Failing here costs one cycle and
        // says why; succeeding costs the alerts silently.
        var request = new AlertReconciliationRequest
        {
            Scope = "   ",
            Observed = [Psu()],
            NowUtc = Cycle(0),
        };

        Assert.Throws<ArgumentException>(() => AlertReconciler.Reconcile(request));
    }

    [Fact]
    public void Reconciling_nothing_against_nothing_is_not_an_error()
    {
        var result = Run([]);

        Assert.Empty(result.Instances);
        Assert.Empty(result.ToNotify);
        Assert.Empty(result.Retired);
    }
}
