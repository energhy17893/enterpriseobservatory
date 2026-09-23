using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

public class AlertReconcilerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Cycle(int n) => T0.AddSeconds(30 * n);

    private static readonly TimeSpan Raw = TimeSpan.FromDays(2);

    /// <summary>vc-1 answered and owns esx01.</summary>
    private static readonly EvidenceSources Reporting = new()
    {
        Reporting = ["vc-1"],
        OwnerOf = entity => entity.Value == "esx01" ? "vc-1" : null,
    };

    /// <summary>The direct producer of <see cref="Psu"/> and <see cref="Fan"/>.</summary>
    private static readonly ProducerRun Ilo = ProducerRun.Where("ilo", f => f.HasSource("ilo"));

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
        string scope = AlertScopes.Observation,
        bool ran = true) =>
        AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Scope = scope,
            Observed = observed,

            // The iLO collector ran this cycle and speaks for its own alerts:
            // what it did not report is gone (N = 1).
            ProducersRun = ran ? [Ilo] : [],
            Stored = previous?.Instances ?? [],
            FlapHistories = previous?.FlapHistories ?? [],
            Flap = flap ?? FlapPolicy.Default,
            NowUtc = now ?? Cycle(0),
            Evaluations = [],
            Sources = Reporting,
            RawRetention = Raw,
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
    public void A_newly_confirmed_alert_appends_one_transition()
    {
        // Package D's write-path guardrail: a fresh, confirmed alert writes
        // exactly the one "Raised" row its own history gained this cycle.
        var result = Run([Psu()]);

        Assert.Equal(1, result.TransitionsAppended);
    }

    [Fact]
    public void An_unconfirmed_alert_appends_no_transition()
    {
        // A warning needs two consecutive hits to confirm (HysteresisPolicy
        // default). AppendHistory never writes for an alert nobody has been
        // shown yet (PostgresAlertStateStore); the count this reconciler
        // reports must agree, or the metric would claim writes that never
        // happen.
        var result = Run([Psu(AlertSeverity.Warning)]);

        Assert.False(Assert.Single(result.Instances).IsConfirmed);
        Assert.Equal(0, result.TransitionsAppended);
    }

    [Fact]
    public void A_cycle_with_nothing_new_appends_no_further_transitions()
    {
        // The same confirmed alert seen again, unchanged, must not keep
        // appending — a runaway write path is exactly what this counter is
        // for catching, so it must read zero on a quiet cycle.
        var first = Run([Psu()]);

        var second = Run([Psu()], previous: first, now: Cycle(1));

        Assert.Equal(0, second.TransitionsAppended);
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
            Evaluations = [],
            Sources = Reporting,
            RawRetention = Raw,
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
            Evaluations = [],
            Sources = Reporting,
            RawRetention = Raw,
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

    // --- three-valued (ADR-0026) ------------------------------------------

    private const string Rule = "dropped-packets";

    private static readonly ResolutionPolicy Three = new() { ConsecutiveAbsent = 3 };

    private static ConditionPresent Present(AlertDefinition alert, DateTimeOffset? at = null) => new()
    {
        Covers = [alert.Fingerprint],
        Alerts = [alert],
        Entity = alert.Entity,
        EvidenceAtUtc = at ?? Cycle(0),
    };

    private static ConditionAbsent Gone(AlertDefinition alert, DateTimeOffset at) => new()
    {
        Covers = [alert.Fingerprint],
        Entity = alert.Entity,
        EvidenceAtUtc = at,
    };

    private static Unknown NotJudged(AlertDefinition alert) => new()
    {
        Covers = [alert.Fingerprint],
        Entity = alert.Entity,
        Reason = UnknownReason.NotJudgeable,
        Detail = "packets below 100/s",
    };

    private static AlertReconciliationResult Rules(
        AlertReconciliationResult? previous,
        DateTimeOffset now,
        IReadOnlyList<SubjectVerdict> verdicts,
        IReadOnlyList<AlertDefinition>? observed = null,
        EvidenceSources? sources = null,
        TimeSpan? evidenceLimit = null,
        IReadOnlyList<ProducerRun>? ran = null) =>
        AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Scope = AlertScopes.Observation,
            Observed = observed ?? [],
            ProducersRun = ran ?? [],
            Stored = previous?.Instances ?? [],
            FlapHistories = previous?.FlapHistories ?? [],
            NowUtc = now,
            Evaluations = [new RuleEvaluation(Rule, Three, verdicts)],
            Sources = sources ?? Reporting,
            RawRetention = Raw,
            EvidenceLimit = evidenceLimit,
        });

    private static EvidenceSources Answered(params string[] sources) => new()
    {
        Reporting = sources,
        OwnerOf = entity => entity.Value == "esx01" ? "vc-1" : null,
    };

    /// <summary>vc-1 is disabled and so, like a silent source, does not answer.</summary>
    private static EvidenceSources Disabled(params string[] disabled) => new()
    {
        Reporting = [],
        DisabledConnections = disabled,
        OwnerOf = entity => entity.Value == "esx01" ? "vc-1" : null,
    };

    [Fact]
    public void A_rule_alert_carries_the_rule_that_raised_it()
    {
        var r = Rules(null, Cycle(0), [Present(Psu())]);

        Assert.Equal(Rule, Assert.Single(r.Instances).RuleId);
    }

    [Fact]
    public void A_rule_alert_resolves_only_after_the_rules_N_fresh_absences()
    {
        var r = Rules(null, Cycle(0), [Present(Psu())]);
        r = Rules(r, Cycle(1), [Gone(Psu(), Cycle(1))]);
        r = Rules(r, Cycle(2), [Gone(Psu(), Cycle(2))]);
        Assert.Equal(AlertLifecycleState.Open, Assert.Single(r.Instances).State);

        r = Rules(r, Cycle(3), [Gone(Psu(), Cycle(3))]);

        Assert.Equal(AlertLifecycleState.Resolved, Assert.Single(r.Instances).State);
    }

    [Fact]
    public void An_alert_its_rule_says_nothing_about_stays_open_as_not_reported()
    {
        // The flipped default: silence from the rule is not "gone".
        var r = Rules(null, Cycle(0), [Present(Psu())]);

        r = Rules(r, Cycle(1), []);

        var instance = Assert.Single(r.Instances);
        Assert.Equal(AlertLifecycleState.Open, instance.State);
        Assert.Equal(UnknownReason.NotReported, instance.StaleReason);
        Assert.Empty(r.Retired);
        Assert.DoesNotContain(r.ToNotify, i => i.PendingNotification != AlertNotificationKind.Raised);
    }

    [Fact]
    public void An_unknown_between_absences_resets_the_count()
    {
        var r = Rules(null, Cycle(0), [Present(Psu())]);
        r = Rules(r, Cycle(1), [Gone(Psu(), Cycle(1))]);
        r = Rules(r, Cycle(2), [Gone(Psu(), Cycle(2))]);
        r = Rules(r, Cycle(3), [NotJudged(Psu())]);
        r = Rules(r, Cycle(4), [Gone(Psu(), Cycle(4))]);
        r = Rules(r, Cycle(5), [Gone(Psu(), Cycle(5))]);

        Assert.Equal(AlertLifecycleState.Open, Assert.Single(r.Instances).State);
    }

    [Fact]
    public void Unknown_never_opens_an_alert()
    {
        var r = Rules(null, Cycle(0), [NotJudged(Psu())]);

        Assert.Empty(r.Instances);
    }

    [Fact]
    public void A_present_verdict_on_a_silent_sources_entity_does_not_open_an_alert()
    {
        // The source clamp applies to present verdicts too: a rule reading a
        // silent vCenter's last-read entities is not evidence of anything now.
        var r = Rules(null, Cycle(0), [Present(Psu())], sources: Answered("vc-2"));

        Assert.Empty(r.Instances);
    }

    [Fact]
    public void An_absent_verdict_on_a_silent_sources_entity_marks_the_alert_stale_and_keeps_it_open()
    {
        var r = Rules(null, Cycle(0), [Present(Psu())]);

        r = Rules(r, Cycle(1), [Gone(Psu(), Cycle(1)) with { Resolution = ResolutionPolicy.Immediate }],
            sources: Answered("vc-2"));

        var instance = Assert.Single(r.Instances);
        Assert.Equal(AlertLifecycleState.Open, instance.State);
        Assert.Equal(UnknownReason.SourceSilent, instance.StaleReason);
    }

    [Fact]
    public void A_present_verdict_on_a_disabled_sources_entity_does_not_open_an_alert()
    {
        // A disabled connection is the same clamp as a silent one, checked
        // first (N1, ADR-0026): a decision, not a failure, but still not
        // evidence about the condition either way.
        var r = Rules(null, Cycle(0), [Present(Psu())], sources: Disabled("vc-1"));

        Assert.Empty(r.Instances);
    }

    [Fact]
    public void Disabling_a_sources_connection_marks_its_open_alert_unknown_not_stale_open()
    {
        // The widened N1 case: SVT_Vcenter's "Host forwards no logs" stayed
        // Open and merely stale for as long as the rule gave no verdict about
        // it -- the age clamp is days out. Disabling has to say why at once.
        var r = Rules(null, Cycle(0), [Present(Psu())]);

        // The rule itself never looks at a disabled source's entities (its
        // read comes from the source, not the reconciler) -- so no verdict at
        // all reaches this fingerprint this cycle, the same shape RemoteLoggingRule
        // left behind.
        r = Rules(r, Cycle(1), [], sources: Disabled("vc-1"));

        var instance = Assert.Single(r.Instances);
        Assert.Equal(AlertLifecycleState.Open, instance.State);
        Assert.Equal(UnknownReason.SourceDisabled, instance.StaleReason);
    }

    [Fact]
    public void Re_enabling_a_source_is_judged_fresh_again()
    {
        // Once vc-1 answers again, its verdict is ordinary evidence: still
        // present reopens exactly as before, resting on nothing left over
        // from having been disabled.
        var r = Rules(null, Cycle(0), [Present(Psu())]);
        r = Rules(r, Cycle(1), [], sources: Disabled("vc-1"));
        r = Rules(r, Cycle(2), [Present(Psu())]);

        var stillPresent = Assert.Single(r.Instances);
        Assert.Equal(AlertLifecycleState.Open, stillPresent.State);
        Assert.Null(stillPresent.StaleReason);

        // And if the condition actually cleared while it was disabled, the
        // next real answer resolves it rather than reopening it blind.
        r = Rules(
            r, Cycle(3), [Gone(Psu(), Cycle(3)) with { Resolution = ResolutionPolicy.Immediate }]);

        Assert.Equal(AlertLifecycleState.Resolved, Assert.Single(r.Instances).State);
    }

    [Fact]
    public void A_verdict_on_evidence_older_than_the_scope_allows_is_input_stale()
    {
        var r = Rules(null, Cycle(0), [Present(Psu())]);

        r = Rules(
            r,
            Cycle(10),
            [Gone(Psu(), Cycle(1)) with { Resolution = ResolutionPolicy.Immediate }],
            evidenceLimit: TimeSpan.FromSeconds(84));

        var instance = Assert.Single(r.Instances);
        Assert.Equal(AlertLifecycleState.Open, instance.State);
        Assert.Equal(UnknownReason.InputStale, instance.StaleReason);

        // Package D's age-bucket hit counter (ADR-0026 §Z3): this cycle's one
        // clamped verdict must show up in the metrics view, not just in the
        // instance itself.
        Assert.Equal(1, r.AgeClampedToUnknown);
    }

    [Fact]
    public void A_verdict_clamped_by_a_silent_source_does_not_count_as_an_age_clamp()
    {
        // Package D's counter is specifically the age rule (Z3), not "any
        // Unknown": a source that stopped reporting is a different problem
        // with a different fix, and conflating the two in one number would
        // make the age-clamp counter lie about which one is happening.
        var r = Rules(null, Cycle(0), [Present(Psu())]);

        r = Rules(
            r,
            Cycle(1),
            [Gone(Psu(), Cycle(1)) with { Resolution = ResolutionPolicy.Immediate }],
            sources: Answered("vc-2"));

        Assert.Equal(UnknownReason.SourceSilent, Assert.Single(r.Instances).StaleReason);
        Assert.Equal(0, r.AgeClampedToUnknown);
    }

    [Fact]
    public void A_direct_producers_alert_on_a_silent_sources_entity_is_kept_open_not_resolved()
    {
        // EntityGraph.Merge keeps a silent vCenter's entities because we did
        // not look. Resolving their alerts on the same cycle said the opposite
        // about the same machines: every finding closed after one missed read.
        var first = Run([Psu()], now: Cycle(0));

        var second = Rules(first, Cycle(1), [], sources: Answered("vc-2"), ran: [Ilo]);

        var instance = Assert.Single(second.Instances);
        Assert.Equal(AlertLifecycleState.Open, instance.State);
        Assert.Equal(UnknownReason.SourceSilent, instance.StaleReason);
        Assert.Empty(second.Retired);

        // Not a cessation either: nobody saw it stop.
        Assert.Empty(second.FlapHistories);
    }

    [Fact]
    public void A_direct_producers_alert_on_a_reporting_sources_entity_still_resolves_at_once()
    {
        var first = Run([Psu()], now: Cycle(0));

        var second = Rules(first, Cycle(1), [], ran: [Ilo]);

        Assert.Equal(AlertLifecycleState.Resolved, Assert.Single(second.Instances).State);
    }

    // --- direct producers sign their cycle (ADR-0026, second PR) ------------

    [Fact]
    public void A_direct_producer_that_did_not_run_leaves_its_alerts_open_and_stale()
    {
        // "Silence is its absence" held only while every producer ran every
        // cycle. A write that was not attempted, a collector that was not
        // polled, a rule that was not run: none of them looked.
        var first = Run([Psu()], now: Cycle(0));

        var second = Run([], previous: first, now: Cycle(1), ran: false);

        var instance = Assert.Single(second.Instances);
        Assert.Equal(AlertLifecycleState.Open, instance.State);
        Assert.True(instance.IsStale);
        Assert.Equal(UnknownReason.NotReported, instance.StaleReason);
        Assert.Contains("no producer", instance.StaleDetail, StringComparison.Ordinal);
        Assert.Empty(second.Retired);
    }

    [Fact]
    public void A_direct_producer_that_ran_and_did_not_see_the_condition_resolves_it_at_once()
    {
        var first = Run([Psu()], now: Cycle(0));

        // Stale first, then the producer runs again: one fresh absence is N.
        var stale = Run([], previous: first, now: Cycle(1), ran: false);
        var ran = Run([], previous: stale, now: Cycle(2));

        Assert.Equal(AlertLifecycleState.Resolved, Assert.Single(ran.Instances).State);
    }

    [Fact]
    public void A_producer_speaks_only_for_its_own_alerts()
    {
        var other = Psu() with { Fingerprint = AlertFingerprint.Create("onboard", "PSU 2 failed", "Hardware", "esx01") };
        var first = Run([Psu(), other], now: Cycle(0));

        var second = Run([], previous: first, now: Cycle(1));

        Assert.Equal(AlertLifecycleState.Resolved, second.Instances.Single(i => i.Fingerprint == Psu().Fingerprint).State);
        var kept = second.Instances.Single(i => i.Fingerprint == other.Fingerprint);
        Assert.Equal(AlertLifecycleState.Open, kept.State);
        Assert.True(kept.IsStale);
    }

    [Fact]
    public void Two_days_without_evidence_move_a_rule_alert_to_unknown_out_of_the_inbox()
    {
        var r = Rules(null, Cycle(0), [Present(Psu())]);

        r = Rules(r, Cycle(1), []);
        r = Rules(r, Cycle(0) + Raw, []);

        var instance = Assert.Single(r.Instances);
        Assert.Equal(AlertLifecycleState.Unknown, instance.State);
        Assert.Empty(r.Visible);
        Assert.DoesNotContain(r.ToNotify, i => i.PendingNotification != AlertNotificationKind.Raised);
    }

    [Fact]
    public void A_restart_with_an_empty_first_cycle_resolves_nothing_and_notifies_nothing()
    {
        // Design note §2's contract: stored open alarms, then one cycle with no
        // observations and no reporting sources. Today's symptom was a dozen
        // alarms resolved on this cycle and re-raised, re-notified, on the next.
        var stored = Run([Psu(), Fan() with { Severity = AlertSeverity.Critical }], now: Cycle(0));
        stored = Rules(stored, Cycle(1), [Present(Psu()), Present(Fan() with { Severity = AlertSeverity.Critical })]);
        stored = stored with { Instances = [.. stored.Instances.Select(AlertLifecycle.MarkNotified)] };
        Assert.Equal(2, stored.Instances.Count(i => i.IsVisible));

        var first = AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Scope = AlertScopes.Observation,
            Stored = stored.Instances,
            FlapHistories = stored.FlapHistories,
            NowUtc = Cycle(2),
            Evaluations = [new RuleEvaluation(Rule, Three, [Gone(Psu(), Cycle(2)) with { Resolution = ResolutionPolicy.Immediate }])],
            Sources = Answered(),
            RawRetention = Raw,
        });

        Assert.DoesNotContain(first.Instances, i => i.State == AlertLifecycleState.Resolved);
        Assert.Empty(first.Retired);
        Assert.Empty(first.ToNotify);
        Assert.Equal(2, first.Instances.Count(i => i.IsVisible));
    }

    [Fact]
    public void A_rule_that_failed_keeps_its_alerts_open_as_rule_failed()
    {
        var r = Rules(null, Cycle(0), [Present(Psu())]);

        var failed = Analysis.GuardedRule.Run(
            Rule,
            () => throw new InvalidOperationException("boom"),
            [new HeldAlert(Psu().Fingerprint, Psu().Entity)]);

        r = Rules(r, Cycle(1), failed.Verdicts, observed: failed.Failures);

        var psu = r.Instances.Single(i => i.Title == "PSU 2 failed");
        Assert.Equal(AlertLifecycleState.Open, psu.State);
        Assert.Equal(UnknownReason.RuleFailed, psu.StaleReason);
        Assert.Contains(r.Instances, i => i.Title == "Analysis rule failed");
    }

    [Fact]
    public void A_resolved_alert_of_a_rule_no_longer_registered_retires_and_an_open_one_stays()
    {
        var r = Rules(null, Cycle(0), [Present(Psu()), Present(Fan() with { Severity = AlertSeverity.Critical })]);
        r = r with
        {
            Instances =
            [
                .. r.Instances.Select(i => i.Title == "PSU 2 failed"
                    ? AlertLifecycle.MoveToFinding(i, Cycle(1))
                    : i),
            ],
        };

        var next = AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Scope = AlertScopes.Observation,
            Stored = r.Instances,
            NowUtc = Cycle(2),
            Evaluations = [],
            Sources = Reporting,
            RawRetention = Raw,
        });

        Assert.Equal([Psu().Fingerprint], next.Retired);
        Assert.Equal(AlertLifecycleState.Open, Assert.Single(next.Instances).State);
    }

    // --- retirement (ADR-0026 design note §2, housekeeping) ------------------

    [Fact]
    public void An_open_alert_of_a_rule_no_longer_registered_resolves_as_rule_retired_then_retires()
    {
        // K2's moved alarms that nothing moved stayed "not reported" forever.
        var r = Rules(null, Cycle(0), [Present(Psu())]);

        AlertReconciliationResult Next(AlertReconciliationResult previous, int n) =>
            AlertReconciler.Reconcile(new AlertReconciliationRequest
            {
                Scope = AlertScopes.Observation,
                Stored = previous.Instances,
                NowUtc = Cycle(n),
                Evaluations = [],
                Sources = Reporting,
                RawRetention = Raw,
                RegisteredRules = ["some-other-rule"],
            });

        // One cycle unreported first: the path that retired the rule (K2's
        // move to a finding) gets it to close the alarm its own way.
        var unreported = Next(r, 1);
        Assert.Equal(AlertLifecycleState.Open, Assert.Single(unreported.Instances).State);
        Assert.Equal(UnknownReason.NotReported, Assert.Single(unreported.Instances).StaleReason);

        var retired = Next(unreported, 2);
        var psu = Assert.Single(retired.Instances);
        Assert.Equal(AlertLifecycleState.Resolved, psu.State);
        Assert.Equal(AlertTransitionReason.RuleRetired, psu.History[^1].Reason);
        Assert.Empty(retired.ToNotify);

        Assert.Equal([Psu().Fingerprint], Next(retired, 3).Retired);
    }

    [Fact]
    public void Without_the_registered_rules_nothing_is_called_retired()
    {
        // The forgetful call is the safe one: no roster, no retirement.
        var r = Rules(null, Cycle(0), [Present(Psu())]);

        var next = Rules(r, Cycle(1), []);

        Assert.Equal(AlertLifecycleState.Open, Assert.Single(next.Instances).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_alert_on_a_vanished_entity_resolves_as_subject_removed(bool ofARule)
    {
        var r = ofARule ? Rules(null, Cycle(0), [Present(Psu())]) : Run([Psu()], now: Cycle(0));
        var vanished = Reporting with { IsVanished = e => e.Value == "esx01" };

        var next = Rules(r, Cycle(1), [], sources: vanished);

        var psu = Assert.Single(next.Instances);
        Assert.Equal(AlertLifecycleState.Resolved, psu.State);
        Assert.Equal(AlertTransitionReason.SubjectRemoved, psu.History[^1].Reason);
    }

    [Fact]
    public void A_vanished_entity_does_not_override_a_present_verdict()
    {
        var vanished = Reporting with { IsVanished = e => e.Value == "esx01" };

        var r = Rules(null, Cycle(0), [Present(Psu())], sources: vanished);

        Assert.Equal(AlertLifecycleState.Open, Assert.Single(r.Instances).State);
    }
}