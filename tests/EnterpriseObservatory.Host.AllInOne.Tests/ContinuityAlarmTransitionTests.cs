using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// The one-time move of open M8 alarms to continuity findings (ADR-0024 §5,
/// K2), through the real cycle, service and stores the worker drives.
/// </summary>
public class ContinuityAlarmTransitionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static readonly EntityId ClusterId = new("vc-1:domain-c1");

    private readonly TestClock _clock = new(T0);
    private readonly InMemoryEntityGraphStore _graphs = new();
    private readonly InMemoryAlertStateStore _alerts = new();
    private readonly InMemoryComplianceStore _findings = new();

    private static MonitoringOptions Options { get; } = new()
    {
        Collection = CollectionPolicy.Default with { MaxRetries = 0 },
    };

    private MonitoringCycle Cycle() => new(
        new InventoryCollectionPipeline(_clock),
        new ObservationCollectionPipeline(_clock),
        _graphs,
        _alerts,
        new InMemoryCollectorHealthStore(),
        new InMemoryCoverageStore(),
        new RecordingNotifier(),
        new InMemoryObservationStore(),
        new InMemoryMaintenanceWindowStore(),
        _clock,
        new InMemoryEventStore());

    private ComplianceService Compliance() => new(
        [
            ComplianceCatalogue.Unavailable("no vendor guide in this test"),
            ContinuityCatalogue.Build(ContinuityCatalogue.Production),
        ],
        _findings,
        _clock,
        ContinuityCatalogue.ChecksById(ContinuityCatalogue.Production));

    /// <summary>The fingerprint the retired HA rule gave "admission control disabled".</summary>
    private static readonly AlertFingerprint AdmissionAlarm = AlertFingerprint.Create(
        "platform", "Cluster admission control is disabled", "Configuration", ClusterId.Value,
        "cluster-ha-scorecard-admission-control-disabled");

    private static readonly AlertFingerprint HaDisabledAlarm = AlertFingerprint.Create(
        "platform", "Cluster has no vSphere HA protection", "Configuration", ClusterId.Value,
        "cluster-ha-scorecard-ha-disabled");

    private void Seed(params AlertInstance[] instances) =>
        _alerts.Reconcile(AlertScopes.Inventory, (_, _) => new AlertReconciliationResult { Instances = instances });

    private static AlertInstance Alarm(AlertFingerprint fingerprint, AlertLifecycleState state, string title) => new()
    {
        Fingerprint = fingerprint,

        // What migration 14 fills in from the fingerprint's check id: the
        // retired rule still owns the alarm, so a cycle that no longer
        // evaluates that rule leaves it "not reported", open.
        RuleId = MovedContinuityRules.RuleOf(fingerprint.Value),
        Severity = AlertSeverity.Warning,
        State = state,
        Title = title,
        Entity = ClusterId,
        Scope = AlertScopes.Inventory,
        Category = "Configuration",
        Source = "platform",
        ConsecutiveHits = 5,
        IsConfirmed = true,
        ClearedByOperator = false,
        PendingNotification = AlertNotificationKind.None,
        FirstSeenUtc = T0.AddDays(-1),
        LastSeenUtc = T0.AddHours(-1),
        SilencedUntilUtc = state == AlertLifecycleState.Silenced ? T0.AddDays(60) : null,
    };

    private FakeInventorySource ClusterWithAdmissionControlOff() => new("vc-1")
    {
        Behaviour = () => new InventorySnapshot
        {
            SourceInstanceId = "vc-1",
            ReadAtUtc = _clock.UtcNow,
            Entities =
            [
                new Entity
                {
                    Id = ClusterId,
                    Kind = EntityKind.Cluster,
                    DisplayName = "prod",
                    SourceInstanceId = "vc-1",
                    LastSeenUtc = _clock.UtcNow,
                    Settings = new Dictionary<string, string>
                    {
                        ["dasConfig.enabled"] = "true",
                        ["dasConfig.admissionControlEnabled"] = "false",
                    },
                },
            ],
        },
    };

    [Fact]
    public async Task The_inventory_cycle_keeps_a_moved_rules_alarm_rather_than_resolving_it_as_cleared()
    {
        Seed(Alarm(AdmissionAlarm, AlertLifecycleState.Open, "Cluster admission control is disabled"));

        await Cycle().RunInventoryAsync([ClusterWithAdmissionControlOff()], Options, CancellationToken.None);

        var alarm = Assert.Single(_alerts.All);
        Assert.Equal(AlertLifecycleState.Open, alarm.State);
        Assert.DoesNotContain(alarm.History, t => t.Reason == AlertTransitionReason.ConditionCleared);
    }

    [Fact]
    public async Task After_the_first_continuity_evaluation_the_alarm_is_resolved_as_moved_and_its_finding_exists()
    {
        Seed(Alarm(AdmissionAlarm, AlertLifecycleState.Open, "Cluster admission control is disabled"));

        await Cycle().RunInventoryAsync([ClusterWithAdmissionControlOff()], Options, CancellationToken.None);

        var compliance = Compliance();
        var graph = _graphs.Current;
        compliance.Evaluate([.. graph.Active], ["vc-1"], graph);

        var moved = Assert.Single(ContinuityAlarmTransition.Run(_alerts, compliance.Findings(), _clock.UtcNow));

        Assert.Equal(AlertLifecycleState.Resolved, moved.Alarm.State);
        Assert.Equal(AlertTransitionReason.MovedToFinding, moved.Alarm.History[^1].Reason);

        var finding = Assert.IsType<ComplianceFinding>(moved.Finding);
        Assert.Equal(ContinuityControls.HaAdmissionControl, finding.ControlId);
        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Contains(compliance.Findings(), f => f == finding);

        // Once: a second run finds nothing left to move.
        Assert.Empty(ContinuityAlarmTransition.Run(_alerts, compliance.Findings(), _clock.UtcNow));

        // And the next inventory cycle retires the resolved alarm normally.
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Cycle().RunInventoryAsync([ClusterWithAdmissionControlOff()], Options, CancellationToken.None);
        Assert.Empty(_alerts.All);
    }

    [Fact]
    public async Task A_silence_on_a_moved_alarm_is_not_carried_over_as_an_acceptance()
    {
        Seed(
            Alarm(AdmissionAlarm, AlertLifecycleState.Silenced, "Cluster admission control is disabled"),
            Alarm(HaDisabledAlarm, AlertLifecycleState.Acknowledged, "Cluster has no vSphere HA protection"));

        await Cycle().RunInventoryAsync([ClusterWithAdmissionControlOff()], Options, CancellationToken.None);

        var compliance = Compliance();
        var graph = _graphs.Current;
        compliance.Evaluate([.. graph.Active], ["vc-1"], graph);

        var moved = ContinuityAlarmTransition.Run(_alerts, compliance.Findings(), _clock.UtcNow);

        Assert.Equal(2, moved.Count);
        Assert.All(moved, m => Assert.Equal(AlertLifecycleState.Resolved, m.Alarm.State));

        var admission = compliance.Findings().Single(f => f.ControlId == ContinuityControls.HaAdmissionControl);
        Assert.Equal(ComplianceVerdict.Failing, admission.Verdict);
        Assert.Null(admission.Acceptance);
        Assert.Equal(FindingState.Failing, admission.StateAt(compliance.Exceptions(), _clock.UtcNow));
        Assert.Empty(compliance.Exceptions());
    }
}
