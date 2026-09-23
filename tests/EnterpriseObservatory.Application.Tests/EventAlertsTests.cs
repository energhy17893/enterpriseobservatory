using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// vCenter events turned into alerts, through one table.
/// </summary>
/// <remarks>
/// An event is a moment and an alert is a state, so almost everything here is
/// about the boundary between the two: when a report opens something, which
/// restoration closes it and which must not, and how long a report with no
/// restoration is believed. The messages are the shapes Broadcom's KBs quote,
/// because the instance pairing reads them.
/// </remarks>
public class EventAlertsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly EventObjectRef Esx01 = new() { MoRef = "host-1", Name = "esx01" };
    private static readonly EventObjectRef Esx02 = new() { MoRef = "host-2", Name = "esx02" };
    private static readonly EventObjectRef Prod = new() { MoRef = "domain-c7", Name = "prod" };

    private static SourceEvent Event(
        long key,
        string type,
        DateTimeOffset? at = null,
        string message = "",
        EventObjectRef? host = null,
        EventObjectRef? cluster = null,
        EventObjectRef? vm = null,
        string source = "vc-1") => new()
        {
            SourceInstanceId = source,
            Key = key,
            CreatedAtUtc = at ?? T0.AddMinutes(-10),
            EventClass = "EventEx",
            TypeId = type,
            Message = message,
            Host = host,
            ComputeResource = cluster,
            VirtualMachine = vm,
        };

    private static string LinkDown(string nic) => $"Physical NIC {nic} linkstate is down.";

    private static string LinkUp(string nic) => $"Physical NIC {nic} linkstate is up.";

    private static IReadOnlyList<AlertDefinition> Evaluate(params SourceEvent[] events) =>
        EventAlerts.Evaluate(events, T0);

    // --- opening --------------------------------------------------------------

    [Fact]
    public void An_isolated_host_is_a_critical_alert_on_that_host()
    {
        var alert = Assert.Single(Evaluate(
            Event(1, "com.vmware.vc.HA.DasHostIsolatedEvent", host: Esx01, cluster: Prod)));

        Assert.Equal("HA host isolated", alert.Title);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(new EntityId("vc-1:host-1"), alert.Entity);
        Assert.Equal("High availability", alert.Category);
        Assert.True(alert.IsDerived);
    }

    [Fact]
    public void An_event_type_outside_the_table_raises_nothing()
    {
        Assert.Empty(Evaluate(Event(1, "VmPoweredOffEvent", host: Esx01)));
    }

    [Fact]
    public void The_standard_class_and_its_ha_twin_for_one_host_are_one_alert()
    {
        var alert = Assert.Single(Evaluate(
            Event(1, "DasHostFailedEvent", host: Esx01, cluster: Prod),
            Event(2, "com.vmware.vc.HA.DasHostFailedEvent", host: Esx01, cluster: Prod)));

        Assert.Equal("HA host failed", alert.Title);
    }

    [Fact]
    public void Type_ids_match_without_regard_to_case()
    {
        // vCenter's own catalogue carries com.vmware.vc.ha.* beside com.vmware.vc.HA.*.
        Assert.Single(Evaluate(Event(1, "com.vmware.vc.ha.dashostfailedevent", host: Esx01)));
    }

    [Fact]
    public void A_failed_failover_is_about_the_virtual_machine()
    {
        var alert = Assert.Single(Evaluate(Event(
            1, "VmFailoverFailed",
            host: Esx01, cluster: Prod, vm: new EventObjectRef { MoRef = "vm-9", Name = "db01" })));

        Assert.Equal(new EntityId("vc-1:vm-9"), alert.Entity);
        Assert.Contains("'db01'", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_without_its_subject_falls_back_to_the_cluster()
    {
        // The standard HA classes name their host in a field the collector does
        // not read. Dropping the event would be an isolation nobody was told of.
        var alert = Assert.Single(Evaluate(Event(1, "DasHostIsolatedEvent", cluster: Prod)));

        Assert.Equal(new EntityId("vc-1:domain-c7"), alert.Entity);
    }

    [Fact]
    public void An_event_naming_nothing_is_still_reported_against_its_vcenter()
    {
        var alert = Assert.Single(Evaluate(Event(1, "DasHostIsolatedEvent")));

        Assert.Null(alert.Entity);
        Assert.Contains("vCenter 'vc-1'", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_quotes_vcenters_own_message()
    {
        var alert = Assert.Single(Evaluate(
            Event(1, "esx.problem.net.vmnic.linkstate.down", host: Esx01, message: LinkDown("vmnic3"))));

        Assert.Contains("\"Physical NIC vmnic3 linkstate is down.\"", alert.Description, StringComparison.Ordinal);
        Assert.Contains("esx.clear.net.vmnic.linkstate.up", alert.Description, StringComparison.Ordinal);
    }

    // --- closing --------------------------------------------------------------

    [Fact]
    public void A_restoration_after_the_loss_closes_it()
    {
        Assert.Empty(Evaluate(
            Event(1, "esx.problem.net.redundancy.lost", T0.AddMinutes(-20), host: Esx01),
            Event(2, "esx.clear.net.redundancy.restored", T0.AddMinutes(-10), host: Esx01)));
    }

    [Fact]
    public void A_loss_after_the_restoration_is_open_again()
    {
        Assert.Single(Evaluate(
            Event(1, "esx.problem.net.redundancy.lost", T0.AddMinutes(-30), host: Esx01),
            Event(2, "esx.clear.net.redundancy.restored", T0.AddMinutes(-20), host: Esx01),
            Event(3, "esx.problem.net.redundancy.lost", T0.AddMinutes(-10), host: Esx01)));
    }

    [Fact]
    public void Within_one_second_the_event_number_decides_the_order()
    {
        var at = T0.AddMinutes(-10);

        Assert.Empty(Evaluate(
            Event(10, "esx.problem.net.redundancy.lost", at, host: Esx01),
            Event(11, "esx.clear.net.redundancy.restored", at, host: Esx01)));

        Assert.Single(Evaluate(
            Event(11, "esx.problem.net.redundancy.lost", at, host: Esx01),
            Event(10, "esx.clear.net.redundancy.restored", at, host: Esx01)));
    }

    [Fact]
    public void A_restoration_on_another_host_does_not_close_it()
    {
        Assert.Single(Evaluate(
            Event(1, "esx.problem.net.redundancy.lost", T0.AddMinutes(-20), host: Esx01),
            Event(2, "esx.clear.net.redundancy.restored", T0.AddMinutes(-10), host: Esx02)));
    }

    [Fact]
    public void A_restoration_from_another_vcenter_does_not_close_it()
    {
        // host-1 in one vCenter and host-1 in another are different machines.
        Assert.Single(Evaluate(
            Event(1, "esx.problem.net.redundancy.lost", T0.AddMinutes(-20), host: Esx01),
            Event(2, "esx.clear.net.redundancy.restored", T0.AddMinutes(-10), host: Esx01, source: "vc-2")));
    }

    [Fact]
    public void The_restoration_of_another_condition_does_not_close_it()
    {
        Assert.Single(Evaluate(
            Event(1, "esx.problem.net.redundancy.lost", T0.AddMinutes(-20), host: Esx01),
            Event(2, "esx.clear.net.dvport.redundancy.restored", T0.AddMinutes(-10), host: Esx01)));
    }

    [Fact]
    public void One_vmnic_coming_back_does_not_close_another()
    {
        var alert = Assert.Single(Evaluate(
            Event(1, "esx.problem.net.vmnic.linkstate.down", T0.AddMinutes(-20), LinkDown("vmnic2"), Esx01),
            Event(2, "esx.problem.net.vmnic.linkstate.down", T0.AddMinutes(-20), LinkDown("vmnic3"), Esx01),
            Event(3, "esx.clear.net.vmnic.linkstate.up", T0.AddMinutes(-10), LinkUp("vmnic2"), Esx01)));

        Assert.Contains("vmnic3", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_vmnics_down_are_two_alerts()
    {
        var alerts = Evaluate(
            Event(1, "esx.problem.net.vmnic.linkstate.down", message: LinkDown("vmnic2"), host: Esx01),
            Event(2, "esx.problem.net.vmnic.linkstate.down", message: LinkDown("vmnic3"), host: Esx01));

        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void A_restoration_naming_no_instance_does_not_close_one_that_named_it()
    {
        // The safe way to be wrong: held until its time to live, never closed
        // by somebody else's recovery.
        Assert.Single(Evaluate(
            Event(1, "esx.problem.net.vmnic.linkstate.down", T0.AddMinutes(-20), LinkDown("vmnic2"), Esx01),
            Event(2, "esx.clear.net.vmnic.linkstate.up", T0.AddMinutes(-10), "Link state up", Esx01)));
    }

    [Fact]
    public void A_storage_device_is_paired_with_its_own_restoration()
    {
        // The messages as KB 328900 quotes them. The lost one ends its sentence
        // with a full stop straight after the device name; the pattern must not
        // take the stop with it, or the two would never pair.
        var lost = "Lost connectivity to storage device naa.60014052e106910005cd001000000000. " +
            "Path vmhba64:C4:T0:L0 is down. Affected datastores: 'SBS Datastore'.";
        var restored = "Connectivity to storage device naa.60014052e106910005cd001000000000 " +
            "(Datastores: 'SBS Datastore') restored. Path vmhba33:C1:T0:L0 is active again.";
        var other = "Connectivity to storage device naa.6000000000000000000000000000000a " +
            "(Datastores: 'other') restored. Path vmhba33:C1:T0:L1 is active again.";

        Assert.Empty(Evaluate(
            Event(1, "esx.problem.storage.connectivity.lost", T0.AddMinutes(-20), lost, Esx01),
            Event(2, "esx.clear.storage.connectivity.restored", T0.AddMinutes(-10), restored, Esx01)));

        Assert.Single(Evaluate(
            Event(1, "esx.problem.storage.connectivity.lost", T0.AddMinutes(-20), lost, Esx01),
            Event(2, "esx.clear.storage.connectivity.restored", T0.AddMinutes(-10), other, Esx01)));
    }

    [Fact]
    public void An_nfs_mount_is_paired_with_its_own_restoration()
    {
        Assert.Empty(Evaluate(
            Event(1, "esx.problem.vmfs.nfs.server.disconnect", T0.AddMinutes(-20),
                "Lost connection to server 10.0.0.5 mount point /vol/gold mounted as 1a2b-3c4d (gold).", Esx01),
            Event(2, "esx.clear.vmfs.nfs.server.restored", T0.AddMinutes(-10),
                "Restored connection to server 10.0.0.5 mount point /vol/gold mounted as 1a2b-3c4d (gold).", Esx01)));
    }

    [Fact]
    public void Insufficient_failover_resources_close_when_the_level_is_restored()
    {
        Assert.Empty(Evaluate(
            Event(1, "InsufficientFailoverResourcesEvent", T0.AddMinutes(-20), cluster: Prod),
            Event(2, "FailoverLevelRestored", T0.AddMinutes(-10), cluster: Prod)));
    }

    [Fact]
    public void A_lost_ha_master_closes_when_vcenter_reconnects()
    {
        Assert.Single(Evaluate(
            Event(1, "com.vmware.vc.HA.VcCannotFindMasterEvent", T0.AddMinutes(-20), cluster: Prod)));

        Assert.Empty(Evaluate(
            Event(1, "com.vmware.vc.HA.VcCannotFindMasterEvent", T0.AddMinutes(-20), cluster: Prod),
            Event(2, "com.vmware.vc.HA.VcConnectedToMasterEvent", T0.AddMinutes(-10), cluster: Prod)));
    }

    // --- time to live ---------------------------------------------------------

    [Fact]
    public void A_condition_with_no_clear_is_held_for_its_time_to_live_and_no_longer()
    {
        var ttl = EventAlertPolicy.DefaultTimeToLive;

        Assert.Single(Evaluate(
            Event(1, "DasHostFailedEvent", T0 - ttl + TimeSpan.FromSeconds(1), host: Esx01)));

        Assert.Empty(Evaluate(Event(1, "DasHostFailedEvent", T0 - ttl, host: Esx01)));
    }

    [Fact]
    public void A_missed_restoration_does_not_hold_an_alert_open_for_ever()
    {
        Assert.Empty(Evaluate(Event(
            1, "esx.problem.net.redundancy.lost",
            T0 - EventAlertPolicy.DefaultTimeToLive - TimeSpan.FromMinutes(1), host: Esx01)));
    }

    [Fact]
    public void The_time_to_live_runs_from_the_latest_report()
    {
        var ttl = EventAlertPolicy.DefaultTimeToLive;

        var alert = Assert.Single(Evaluate(
            Event(1, "esx.problem.net.vmnic.linkstate.flapping", T0 - ttl - TimeSpan.FromHours(1), LinkDown("vmnic1"), Esx01),
            Event(2, "esx.problem.net.vmnic.linkstate.flapping", T0.AddHours(-1), LinkDown("vmnic1"), Esx01)));

        Assert.Contains("stays open for 24 hours", alert.Description, StringComparison.Ordinal);

        // The report older than the time to live is not counted either: "the
        // latest of 2 reports in the last 24 hours" would be a false sentence.
        Assert.DoesNotContain("latest of", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_row_outlives_the_two_inventory_cycles_a_warning_needs()
    {
        // A warning is confirmed on its second hit. A row whose time to live is
        // shorter than two inventory intervals is a warning that is forgotten
        // before it can be shown.
        var twoCycles = 2 * new Monitoring.MonitoringOptions().InventoryInterval;

        Assert.All(EventAlerts.Catalogue, c => Assert.True(c.TimeToLive > twoCycles, c.Id));
    }

    // --- one alert per condition, not per event -------------------------------

    [Fact]
    public void A_later_report_of_the_same_condition_keeps_the_same_fingerprint()
    {
        // What stops the rule raising the same alert every cycle: nothing
        // time-varying is in the fingerprint, so reconciliation sees one problem.
        var first = Assert.Single(Evaluate(Event(1, "DasHostIsolatedEvent", T0.AddHours(-2), host: Esx01)));
        var again = Assert.Single(Evaluate(
            Event(1, "DasHostIsolatedEvent", T0.AddHours(-2), host: Esx01),
            Event(7, "DasHostIsolatedEvent", T0.AddMinutes(-5), host: Esx01)));

        Assert.Equal(first.Fingerprint, again.Fingerprint);
        Assert.Contains("latest of 2 reports", again.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Re_evaluating_the_same_events_gives_the_same_alerts()
    {
        SourceEvent[] events =
        [
            Event(1, "DasHostFailedEvent", host: Esx01),
            Event(2, "esx.problem.net.vmnic.linkstate.down", message: LinkDown("vmnic0"), host: Esx02),
        ];

        Assert.Equal(
            EventAlerts.Evaluate(events, T0).Select(a => a.Fingerprint.Value).Order(StringComparer.Ordinal),
            EventAlerts.Evaluate(events, T0.AddMinutes(5)).Select(a => a.Fingerprint.Value).Order(StringComparer.Ordinal));
    }

    // --- the table ------------------------------------------------------------

    [Fact]
    public void A_type_that_raises_two_conditions_is_a_broken_table()
    {
        var row = EventAlerts.Catalogue[0];
        var policy = new EventAlertPolicy { Conditions = [row, row with { Id = "copy" }] };

        Assert.Throws<InvalidOperationException>(() => EventAlerts.Evaluate([], T0, policy));
    }

    [Fact]
    public void A_type_that_both_raises_and_clears_the_same_row_is_a_broken_table()
    {
        var row = EventAlerts.Catalogue[0];
        var policy = new EventAlertPolicy { Conditions = [row with { ClearedBy = [row.RaisedBy[0]] }] };

        Assert.Throws<InvalidOperationException>(() => EventAlerts.Evaluate([], T0, policy));
    }

    [Fact]
    public void A_broken_table_costs_the_rule_not_the_cycle()
    {
        var row = EventAlerts.Catalogue[0];
        var policy = new EventAlertPolicy { Conditions = [row, row with { Id = "copy" }] };

        var alert = Assert.Single(GuardedRule.Run(
            EventAlerts.RuleId, () => [.. EventAlerts.Evaluate([], T0, policy).Select(a => (SubjectVerdict)new ConditionPresent
            {
                Covers = [a.Fingerprint],
                Alerts = [a],
                EvidenceAtUtc = T0,
            })]).Failures);

        Assert.Equal("Analysis rule failed", alert.Title);
    }

    [Fact]
    public void The_store_is_asked_for_every_type_in_the_table_over_the_longest_time_to_live()
    {
        var store = new AskingStore();
        var policy = new EventAlertPolicy
        {
            Conditions =
            [
                EventAlerts.Catalogue.Single(c => c.Id == "ha-host-failed"),
                EventAlerts.Catalogue.Single(c => c.Id == "uplink-redundancy-lost") with { TimeToLive = TimeSpan.FromDays(3) },
            ],
        };

        EventAlerts.Read(store, T0, policy);

        Assert.Equal(T0.AddDays(-3), store.Since);
        Assert.Equal(
            [
                "DasHostFailedEvent",
                "com.vmware.vc.HA.DasHostFailedEvent",
                "esx.clear.net.redundancy.restored",
                "esx.problem.net.redundancy.lost",
            ],
            store.Types.Order(StringComparer.Ordinal));
    }

    private sealed class AskingStore : IEventStore
    {
        public IReadOnlyList<string> Types { get; private set; } = [];

        public DateTimeOffset Since { get; private set; }

        public IReadOnlyList<EventCursor> Cursors => [];

        public void Record(string sourceInstanceId, IReadOnlyList<SourceEvent> events, bool complete, DateTimeOffset readAtUtc) =>
            throw new NotSupportedException();

        public void RecordFailure(string sourceInstanceId, string detail, DateTimeOffset attemptedAtUtc) =>
            throw new NotSupportedException();

        public EventPage Recent(int offset, int limit, string? sourceInstanceId = null, string? search = null) => EventPage.Empty;

        public int Prune(DateTimeOffset createdBeforeUtc) => 0;

        public IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc)
        {
            Types = [.. typeIds];
            Since = createdSinceUtc;
            return [];
        }
    }

    // --- three values (ADR-0026) --------------------------------------------

    private static SubjectVerdict JudgeOne(params SourceEvent[] events) =>
        Assert.Single(EventAlerts.Judge(events, T0, []));

    [Fact]
    public void An_open_condition_is_present()
    {
        var present = Assert.IsType<ConditionPresent>(JudgeOne(
            Event(1, "DasHostFailedEvent", host: Esx01, cluster: Prod)));

        Assert.Equal(new EntityId("vc-1:host-1"), present.Entity);
        Assert.Equal(T0.AddMinutes(-10), present.EvidenceAtUtc);
    }

    [Fact]
    public void A_documented_clear_at_least_as_new_as_the_report_is_absent()
    {
        var absent = Assert.IsType<ConditionAbsent>(JudgeOne(
            Event(1, "esx.problem.net.redundancy.lost", T0.AddMinutes(-10), host: Esx01),
            Event(2, "esx.clear.net.redundancy.restored", T0.AddMinutes(-5), host: Esx01)));

        Assert.Equal(AbsenceKind.ConditionCleared, absent.Because);
        Assert.Equal(T0.AddMinutes(-5), absent.EvidenceAtUtc);
    }

    [Fact]
    public void A_condition_with_no_documented_clear_expires_as_an_absence()
    {
        var ttl = EventAlertPolicy.DefaultTimeToLive;

        var absent = Assert.IsType<ConditionAbsent>(JudgeOne(
            Event(1, "DasHostFailedEvent", T0 - ttl, host: Esx01)));

        Assert.Equal(AbsenceKind.Expired, absent.Because);
    }

    [Fact]
    public void A_condition_with_a_documented_clear_that_never_arrived_is_insufficient_series_not_absent()
    {
        var ttl = EventAlertPolicy.DefaultTimeToLive;

        var unknown = Assert.IsType<Unknown>(JudgeOne(
            Event(1, "esx.problem.net.redundancy.lost", T0 - ttl, host: Esx01)));

        Assert.Equal(UnknownReason.InsufficientSeries, unknown.Reason);
        Assert.Contains("never arrived", unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_source_keeps_the_alert_open_and_a_fresh_absence_then_resolves_it()
    {
        // N = 1 for this rule, so there is no count to reset -- the point is
        // narrower: a stale source's silence must not be read as a clear, and
        // the alert must survive it untouched.
        var rule = new EventAlertsRule();
        var reader = new SteppingReader();

        reader.Watermark = ReadThrough("vc-1", T0.AddMinutes(-4));
        reader.Events = [Event(1, "esx.problem.net.redundancy.lost", T0.AddMinutes(-10), host: Esx01)];

        IReadOnlyList<AlertInstance> stored = Reconcile(rule, reader, stored: [], T0);
        stored = Reconcile(rule, reader, stored, T0.AddMinutes(1)); // confirm (Warning, 2nd hit)

        Assert.Equal(AlertLifecycleState.Open, Assert.Single(stored).State);

        // The source goes silent: no more events reported and the watermark
        // falls behind. The rule must not call this a clear.
        reader.Events = [];
        reader.Watermark = ReadThrough("vc-1", T0.AddHours(-2));
        stored = Reconcile(rule, reader, stored, T0.AddMinutes(2));

        var stale = Assert.Single(stored);
        Assert.Equal(AlertLifecycleState.Open, stale.State);
        Assert.True(stale.IsStale);
        Assert.Equal(UnknownReason.SourceSilent, stale.StaleReason);

        // The source catches up: a fresh absence, and N = 1 resolves it.
        reader.Watermark = ReadThrough("vc-1", T0.AddMinutes(3));
        stored = Reconcile(rule, reader, stored, T0.AddMinutes(3));

        Assert.DoesNotContain(stored, i => i.State == AlertLifecycleState.Open);
    }

    private static IReadOnlyList<AlertInstance> Reconcile<TRule>(
        TRule rule, IEventReader events, IReadOnlyList<AlertInstance> stored, DateTimeOffset nowUtc)
        where TRule : IAnalysisRule
    {
        var context = new RuleContext
        {
            ReadGraph = () => EntityGraph.Empty,
            NowUtc = nowUtc,
            Options = MonitoringOptions.Default,
            Series = new NoSeries(),
            Events = events,
            HeldBy = ruleId => ruleId == rule.RuleId
                ? [.. stored.Where(i => i.RuleId == ruleId).Select(i => new HeldAlert(i.Fingerprint, i.Entity))]
                : [],
        };

        return AlertReconciler.Reconcile(new AlertReconciliationRequest
        {
            Scope = "inventory",
            Stored = stored,
            NowUtc = nowUtc,
            Evaluations = [new RuleEvaluation(rule.RuleId, rule.Resolution, rule.Evaluate(context))],
            Sources = new EvidenceSources { Reporting = ["vc-1"], OwnerOf = _ => "vc-1" },
            RawRetention = TimeSpan.FromDays(2),
        }).Instances;
    }

    // --- HPE SimpliVity (sd00005179en_us) ---------------------------------------

    private static readonly EventObjectRef Db01 = new() { MoRef = "vm-9", Name = "db01" };

    [Theory]
    [InlineData("com.simplivity.event.arbiter.com.lost", "com.simplivity.event.arbiter.com.ok", AlertSeverity.Critical, "", "")]
    [InlineData("com.simplivity.event.arbiter.lost", "com.simplivity.event.arbiter.connected", AlertSeverity.Critical, "", "")]
    [InlineData(
        "com.simplivity.event.vm.ha.fail", "com.simplivity.event.vm.ha.restore", AlertSeverity.Warning,
        "Storage HA protection lost for db01 on datastore ds-svt", "Storage HA protection restored for db01 on datastore ds-svt")]
    [InlineData(
        "com.simplivity.event.control.node.state.faulty", "com.simplivity.event.control.node.state.faulty.clear", AlertSeverity.Critical,
        "SimpliVity OmniStack system ovc01 in the cluster is unreachable.", "SimpliVity OmniStack system ovc01 is reachable again and isnow active.")]
    [InlineData("com.simplivity.event.vm.data.access.not.optimized", "com.simplivity.event.vm.data.access.optimized", AlertSeverity.Warning, "", "")]
    [InlineData("com.simplivity.event.control.phys.capacity.node.warning","com.simplivity.event.control.phys.capacity.node.within.tolerance", AlertSeverity.Warning, "", "")]
    [InlineData("com.simplivity.event.control.phys.capacity.node.error", "com.simplivity.event.control.phys.capacity.node.within.tolerance", AlertSeverity.Critical, "", "")]
    [InlineData("com.simplivity.event.control.phys.capacity.fd.warning", "com.simplivity.event.control.phys.capacity.fd.within.tolerance", AlertSeverity.Warning, "", "")]
    [InlineData("com.simplivity.event.control.phys.capacity.fd.error", "com.simplivity.event.control.phys.capacity.fd.within.tolerance", AlertSeverity.Critical, "", "")]
    [InlineData("com.simplivity.event.control.upgrade.commit.needed", "com.simplivity.event.control.upgrade.committed", AlertSeverity.Warning, "", "")]    [InlineData("com.simplivity.event.control.upgrade.commit.failed", "com.simplivity.event.control.upgrade.committed", AlertSeverity.Critical, "", "")]
    [InlineData("com.simplivity.event.control.upgrade.commit.failed", "com.simplivity.event.control.rollback.success", AlertSeverity.Critical, "", "")]
    [InlineData("com.simplivity.event.control.upgrade.commit.needed", "com.simplivity.event.control.rollback.success", AlertSeverity.Warning, "", "")]
    [InlineData("com.simplivity.event.control.phys.capacity.swap.usage.warning", "com.simplivity.event.control.phys.capacity.swap.usage.clear", AlertSeverity.Warning, "", "")]
    [InlineData("com.simplivity.event.control.phys.capacity.swap.usage.error", "com.simplivity.event.control.phys.capacity.swap.usage.clear", AlertSeverity.Critical, "", "")]
    public void A_simplivity_condition_raises_and_its_hpe_companion_clears_it(
        string raise, string clear, AlertSeverity severity, string raiseMessage, string clearMessage)
    {
        var raised = Event(1, raise, T0.AddMinutes(-20), raiseMessage, Esx01, Prod, Db01);

        var alert = Assert.Single(Evaluate(raised));
        Assert.Equal(severity, alert.Severity);
        Assert.Equal("SimpliVity", alert.Category);

        Assert.Empty(Evaluate(raised, Event(2, clear, T0.AddMinutes(-10), clearMessage, Esx01, Prod, Db01)));
    }

    [Theory]
    [InlineData("com.simplivity.event.critical.storage.failure", AlertSeverity.Critical)]
    [InlineData("com.simplivity.event.vm.backup.execute.fail", AlertSeverity.Warning)]
    [InlineData("com.simplivity.event.vm.backup.replication.fail", AlertSeverity.Warning)]
    [InlineData("com.simplivity.event.storage.phys.drv.smart.alert", AlertSeverity.Critical)]
    [InlineData("com.simplivity.event.storage.ssd.life.warn", AlertSeverity.Warning)]
    [InlineData("com.simplivity.event.storage.ssd.life.err", AlertSeverity.Critical)]
    [InlineData("com.simplivity.event.failover.failure", AlertSeverity.Critical)]
    public void A_simplivity_condition_hpe_never_clears_is_raised_and_says_so(string raise, AlertSeverity severity)
    {
        var alert = Assert.Single(Evaluate(Event(1, raise, host: Esx01, cluster: Prod, vm: Db01)));

        Assert.Equal(severity, alert.Severity);
        Assert.Contains("sends no event when this ends", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Data_access_not_optimized_then_ha_restore_then_optimized_leaves_nothing_open()
    {
        // The live sequence on one VM, 11:32–11:33Z.
        Assert.Empty(Evaluate(
            Event(1, "com.simplivity.event.vm.data.access.not.optimized", T0.AddMinutes(-3),
                "Data access is not optimized for db01 on datastore ds-svt. Move the VM to one of the following OmniCube systems for best performance: ovc02",
                Esx01, Prod, Db01),
            Event(2, "com.simplivity.event.vm.ha.restore", T0.AddMinutes(-2),
                "Storage HA protection restored for db01 on datastore ds-svt", Esx01, Prod, Db01),
            Event(3, "com.simplivity.event.vm.data.access.optimized", T0.AddMinutes(-2),
                "Data access optimized for db01 on datastore ds-svt", Esx01, Prod, Db01)));
    }

    [Fact]
    public void A_failed_commit_closes_commit_needed_and_opens_commit_failed()
    {
        var alert = Assert.Single(Evaluate(
            Event(1, "com.simplivity.event.control.upgrade.commit.needed", T0.AddMinutes(-30), cluster: Prod),
            Event(2, "com.simplivity.event.control.upgrade.commit.failed", T0.AddMinutes(-20), cluster: Prod)));

        Assert.Equal("SimpliVity software commit failed", alert.Title);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public void A_capacity_error_closes_the_warning_and_opens_the_error_and_back()
    {
        const string P = "com.simplivity.event.control.phys.capacity.node.";
        var warning = Event(1, P + "warning", T0.AddMinutes(-40), host: Esx01, cluster: Prod);
        var error = Event(2, P + "error", T0.AddMinutes(-30), host: Esx01, cluster: Prod);

        var escalated = Assert.Single(Evaluate(warning, error));
        Assert.Equal(AlertSeverity.Critical, escalated.Severity);

        var eased = Assert.Single(Evaluate(warning, error, Event(3, P + "warning", T0.AddMinutes(-20), host: Esx01, cluster: Prod)));
        Assert.Equal(AlertSeverity.Warning, eased.Severity);
    }

    [Fact]
    public void Cross_clears_do_not_depend_on_the_order_of_the_rows()
    {
        const string P = "com.simplivity.event.control.phys.capacity.";
        SourceEvent[] events =
        [
            Event(1, P + "node.warning", T0.AddMinutes(-50), host: Esx01, cluster: Prod),
            Event(2, P + "node.error", T0.AddMinutes(-40), host: Esx01, cluster: Prod),
            Event(3, P + "swap.usage.error", T0.AddMinutes(-35), host: Esx02, cluster: Prod),
            Event(4, P + "swap.usage.warning", T0.AddMinutes(-30), host: Esx02, cluster: Prod),
            Event(5, P + "fd.warning", T0.AddMinutes(-25), cluster: Prod),
            Event(6, "com.simplivity.event.control.upgrade.commit.needed", T0.AddMinutes(-20), cluster: Prod),
            Event(7, "com.simplivity.event.control.upgrade.commit.failed", T0.AddMinutes(-10), cluster: Prod),
        ];

        string[] Open(IReadOnlyList<EventCondition> rows) =>
            [.. EventAlerts.Evaluate(events, T0, new EventAlertPolicy { Conditions = rows })
                .Select(a => $"{a.Fingerprint.Value}|{a.Severity}").Order(StringComparer.Ordinal)];

        var expected = Open(EventAlerts.Catalogue);
        Assert.Equal(4, expected.Length);
        Assert.Equal(expected, Open([.. EventAlerts.Catalogue.Reverse()]));

        for (var seed = 1; seed <= 5; seed++)
        {
            var shuffled = EventAlerts.Catalogue.ToArray();
            new Random(seed).Shuffle(shuffled);
            Assert.Equal(expected, Open(shuffled));
        }
    }

    [Fact]
    public void A_commit_clears_both_commit_needed_and_commit_failed()
    {
        Assert.Empty(Evaluate(
            Event(1, "com.simplivity.event.control.upgrade.commit.needed", T0.AddMinutes(-30), cluster: Prod),
            Event(2, "com.simplivity.event.control.upgrade.commit.failed", T0.AddMinutes(-20), cluster: Prod),
            Event(3, "com.simplivity.event.control.upgrade.committed", T0.AddMinutes(-10), cluster: Prod)));
    }

    [Fact]
    public void One_simplivity_node_coming_back_does_not_close_another()
    {
        Assert.Single(Evaluate(
            Event(1, "com.simplivity.event.control.node.state.faulty", T0.AddMinutes(-20),
                "SimpliVity OmniStack system ovc01 in the cluster is unreachable.", cluster: Prod),
            Event(2, "com.simplivity.event.control.node.state.faulty.clear", T0.AddMinutes(-10),
                "SimpliVity OmniStack system ovc02 is reachable again and isnow active.", cluster: Prod)));
    }

    [Fact]
    public void The_informational_edrsi_event_raises_nothing()
    {
        // Measured: the live event table holds it eleven times.
        Assert.Empty(Evaluate(Event(1, "com.simplivity.event.control.edrsi.active", host: Esx01, cluster: Prod)));
    }

    private sealed class SteppingReader : IEventReader
    {
        public IReadOnlyList<SourceEvent> Events { get; set; } = [];

        public EventReadWatermark? Watermark { get; set; }

        public IReadOnlyList<EventReadWatermark> ReadWatermarks => Watermark is { } w ? [w] : [];

        public IReadOnlyList<SourceEvent> OfTypes(IReadOnlyCollection<string> typeIds, DateTimeOffset createdSinceUtc) =>
            [.. Events.Where(e => typeIds.Contains(e.TypeId, StringComparer.OrdinalIgnoreCase) && e.CreatedAtUtc >= createdSinceUtc)];
    }

    private static EventReadWatermark ReadThrough(string source, DateTimeOffset at) =>
        new() { SourceInstanceId = source, ReadThroughUtc = at };

    private sealed class NoSeries : ISeriesReader
    {
        public SeriesResult Query(SeriesQuery query) => new() { Key = query.Key, Resolution = SeriesResolution.Raw };

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
    }
}
