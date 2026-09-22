using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The M8 continuity rules as <c>eo-continuity-1</c> checks (K2): every
/// visible subject gets a verdict, unread input is not evaluated with its
/// reason, and the review-3 fixes carry over.
/// </summary>
public class ContinuityChecksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly ClusterHighAvailabilityPolicy Ha = ClusterHighAvailabilityPolicy.Default;

    private static readonly ComplianceCatalogue Catalogue = ContinuityCatalogue.Build(ContinuityCatalogue.Production);

    private static readonly IReadOnlyDictionary<string, IComplianceCheck> ById =
        ContinuityCatalogue.ChecksById(ContinuityCatalogue.Production);

    private static IReadOnlyList<ComplianceFinding> Evaluate(
        IReadOnlyList<Entity> estate,
        IReadOnlyList<Relationship>? relationships = null,
        DemandSnapshot? demand = null,
        IReadOnlyCollection<string>? reporting = null,
        IReadOnlyList<ComplianceFinding>? previous = null)
    {
        var graph = EntityGraph.Empty with
        {
            Entities = estate.ToDictionary(e => e.Id, e => e),
            Relationships = relationships ?? [],
        };

        return ComplianceEvaluation.Evaluate(
            Catalogue, estate, previous ?? [], T0,
            reportingSources: reporting, checksById: ById, graph: graph, demand: demand);
    }

    private static ComplianceFinding One(IReadOnlyList<ComplianceFinding> findings, string control, string subject = "") =>
        Assert.Single(findings, f => f.ControlId == control && f.Subject == subject);

    private static Entity Cluster(string id = "vc-1:domain-c1", params (string Key, string Value)[] settings) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.Cluster,
        DisplayName = id,
        LastSeenUtc = T0,
        SourceInstanceId = "vc-1",
        Settings = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
    };

    // --- catalogue ------------------------------------------------------------

    [Fact]
    public void Production_registers_one_control_per_finding_kind_each_with_a_source()
    {
        Assert.Equal(
        [
            "eo-cont.ha-enabled", "eo-cont.ha-admission-control", "eo-cont.ha-host-monitoring",
            "eo-cont.ha-storage-protection", "eo-cont.ha-heartbeat-datastores", "eo-cont.ha-network-warning",
            "eo-cont.drs-rule", "eo-cont.path-single", "eo-cont.path-single-hba", "eo-cont.path-single-target",
            "eo-cont.n-plus-one-cpu", "eo-cont.n-plus-one-mem",
            "eo-cont.maint-cdrom", "eo-cont.maint-consolidation", "eo-cont.maint-single-host-datastore",
            "eo-cont.maint-evc", "eo-cont.cert-esxi", "eo-cont.cert-vcenter",
        ], Catalogue.Controls.Select(c => c.ControlId));

        Assert.All(Catalogue.Controls, c => Assert.False(string.IsNullOrWhiteSpace(c.Source)));
        Assert.Equal("VMware KB 2004739", Catalogue.Controls.Single(c => c.ControlId == HaHeartbeatDatastores).Source);
        Assert.StartsWith("Product policy", Catalogue.Controls.Single(c => c.ControlId == NPlusOneCpu).Source,
            StringComparison.Ordinal);
    }

    // --- HA -------------------------------------------------------------------

    private static readonly string[] HaControls =
    [
        HaEnabled, HaAdmissionControl, HaHostMonitoring, HaStorageProtection, HaHeartbeatDatastores, HaNetworkWarning,
    ];

    [Fact]
    public void A_cluster_whose_configuration_was_not_read_is_not_evaluated_never_ha_disabled()
    {
        var findings = Evaluate([Cluster()]);

        Assert.All(HaControls, control =>
        {
            var finding = One(findings, control);
            Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
            Assert.StartsWith("HA settings not read", finding.Reason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Ha_disabled_fails_and_the_rest_of_the_scorecard_is_not_evaluated()
    {
        var findings = Evaluate([Cluster(settings: [(Ha.EnabledSetting, "false"), (Ha.AdmissionControlEnabledSetting, "false")])]);

        Assert.Equal(ComplianceVerdict.Failing, One(findings, HaEnabled).Verdict);

        foreach (var control in HaControls.Skip(1))
        {
            var finding = One(findings, control);
            Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
            Assert.Contains(HaEnabled, finding.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_well_configured_cluster_passes_every_aspect_it_can_see()
    {
        var findings = Evaluate([Cluster(settings:
        [
            (Ha.EnabledSetting, "true"),
            (Ha.AdmissionControlEnabledSetting, "true"),
            (Ha.HostMonitoringSetting, "enabled"),
            (Ha.ApdResponseSetting, "restartConservative"),
            (Ha.PdlResponseSetting, "restartAggressive"),
            (Ha.HeartbeatDatastoreCandidatePolicySetting, "userSelectedDs"),
            (Ha.HeartbeatDatastoreCountSetting, "2"),
        ])]);

        Assert.All(HaControls, control => Assert.Equal(ComplianceVerdict.Passing, One(findings, control).Verdict));
    }

    [Theory]
    [InlineData("eo-cont.ha-admission-control", "dasConfig.admissionControlEnabled", "false")]
    [InlineData("eo-cont.ha-host-monitoring", "dasConfig.hostMonitoring", "disabled")]
    [InlineData("eo-cont.ha-storage-protection",
        "dasConfig.defaultVmSettings.vmComponentProtectionSettings.vmStorageProtectionForAPD", "disabled")]
    [InlineData("eo-cont.ha-network-warning", "dasConfig.option.das.ignoreRedundantNetWarning", "true")]
    public void A_hole_in_ha_protection_fails_its_own_control(string control, string key, string value)
    {
        var findings = Evaluate([Cluster(settings: [(Ha.EnabledSetting, "true"), (key, value)])]);

        Assert.Equal(ComplianceVerdict.Failing, One(findings, control).Verdict);
    }

    [Fact]
    public void Heartbeat_datastores_are_judged_only_when_chosen_by_hand()
    {
        var one = Evaluate([Cluster(settings:
        [
            (Ha.EnabledSetting, "true"),
            (Ha.HeartbeatDatastoreCandidatePolicySetting, "userSelectedDs"),
            (Ha.HeartbeatDatastoreCountSetting, "1"),
        ])]);

        Assert.Equal(ComplianceVerdict.Failing, One(one, HaHeartbeatDatastores).Verdict);

        var automatic = Evaluate([Cluster(settings:
        [
            (Ha.EnabledSetting, "true"),
            (Ha.HeartbeatDatastoreCandidatePolicySetting, "allFeasibleDsWithUserPreference"),
            (Ha.HeartbeatDatastoreCountSetting, "0"),
        ])]);

        // HA picks its own; the set in use is not read, so no pass either.
        Assert.Equal(ComplianceVerdict.NotEvaluated, One(automatic, HaHeartbeatDatastores).Verdict);
    }

    [Fact]
    public void A_silent_vcenters_cluster_keeps_its_last_verdict_marked_stale()
    {
        var findings = Evaluate([Cluster(settings: (Ha.EnabledSetting, "false"))], reporting: ["vc-2"]);

        var finding = One(findings, HaEnabled);
        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.True(finding.Stale);
    }

    // --- DRS ------------------------------------------------------------------

    private static readonly EntityId HostA = new("vc-1:host-a");
    private static readonly EntityId HostB = new("vc-1:host-b");
    private static readonly EntityId Vm1 = new("vc-1:vm-1");
    private static readonly EntityId Vm2 = new("vc-1:vm-2");

    private static Entity Vm(EntityId id, string? power = "poweredOn") => new()
    {
        Id = id,
        Kind = EntityKind.VirtualMachine,
        DisplayName = id.Value["vc-1:".Length..],
        LastSeenUtc = T0,
        SourceInstanceId = "vc-1",
        Settings = power is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["powerState"] = power },
    };

    private static Entity Host(EntityId id, ObservationState state = ObservationState.Active, params StoragePath[] paths) => new()
    {
        Id = id,
        Kind = EntityKind.EsxiHost,
        DisplayName = id.Value["vc-1:".Length..],
        LastSeenUtc = T0,
        SourceInstanceId = "vc-1",
        ObservationState = state,
        Health = HealthState.Healthy,
        StoragePaths = paths,
    };

    private static Relationship RunsOn(EntityId vm, EntityId host) =>
        new() { From = vm, To = host, Kind = RelationshipKind.RunsOn, ObservedAtUtc = T0 };

    private static Entity DrsCluster(params DrsRule[] rules) =>
        Cluster(settings: (Ha.EnabledSetting, "true")) with { DrsRules = rules };

    private static DrsRule AntiAffinity(string name = "keep-db-apart", string? uuid = "uuid-1") => new()
    {
        Name = name,
        RuleUuid = uuid,
        Kind = DrsRuleKind.AntiAffinity,
        Enabled = true,
        Mandatory = true,
        VirtualMachineEntityIds = [Vm1.Value, Vm2.Value],
    };

    [Fact]
    public void A_violated_rule_fails_under_its_uuid_with_its_name_as_the_label_only()
    {
        var findings = Evaluate(
            [DrsCluster(AntiAffinity()), Host(HostA), Vm(Vm1), Vm(Vm2)],
            [RunsOn(Vm1, HostA), RunsOn(Vm2, HostA)]);

        var finding = One(findings, ContinuityControls.DrsRule, "uuid-1");
        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal("keep-db-apart", finding.SubjectLabel);
        Assert.DoesNotContain("keep-db-apart", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("vm-1 on host-a", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_honoured_rule_passes_and_a_rename_keeps_the_finding_and_its_acceptance()
    {
        var failing = Evaluate(
            [DrsCluster(AntiAffinity()), Host(HostA), Vm(Vm1), Vm(Vm2)],
            [RunsOn(Vm1, HostA), RunsOn(Vm2, HostA)]);

        var accepted = failing.Select(f => f.ControlId == ContinuityControls.DrsRule
            ? f with { Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0, Reason = "licensing" } }
            : f).ToList();

        var renamed = Evaluate(
            [DrsCluster(AntiAffinity(name: "db-nodes-apart")), Host(HostA), Vm(Vm1), Vm(Vm2)],
            [RunsOn(Vm1, HostA), RunsOn(Vm2, HostA)],
            previous: accepted);

        var finding = One(renamed, ContinuityControls.DrsRule, "uuid-1");
        Assert.Equal("db-nodes-apart", finding.SubjectLabel);
        Assert.NotNull(finding.Acceptance);

        var fixedUp = Evaluate(
            [DrsCluster(AntiAffinity()), Host(HostA), Host(HostB), Vm(Vm1), Vm(Vm2)],
            [RunsOn(Vm1, HostA), RunsOn(Vm2, HostB)]);

        Assert.Equal(ComplianceVerdict.Passing, One(fixedUp, ContinuityControls.DrsRule, "uuid-1").Verdict);
    }

    [Fact]
    public void A_rule_with_no_uuid_is_keyed_by_its_name()
    {
        var findings = Evaluate(
            [DrsCluster(AntiAffinity(uuid: null)), Host(HostA), Vm(Vm1), Vm(Vm2)],
            [RunsOn(Vm1, HostA), RunsOn(Vm2, HostA)]);

        Assert.Equal(ComplianceVerdict.Failing, One(findings, ContinuityControls.DrsRule, "keep-db-apart").Verdict);
    }

    [Fact]
    public void A_powered_off_vm_is_not_placement_evidence()
    {
        var findings = Evaluate(
            [DrsCluster(AntiAffinity()), Host(HostA), Vm(Vm1), Vm(Vm2, "poweredOff")],
            [RunsOn(Vm1, HostA), RunsOn(Vm2, HostA)]);

        var finding = One(findings, ContinuityControls.DrsRule, "uuid-1");
        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.NotNull(finding.Reason);
    }

    [Fact]
    public void A_disabled_rule_is_not_evaluated_because_drs_does_not_enforce_it()
    {
        var findings = Evaluate(
            [DrsCluster(AntiAffinity() with { Enabled = false }), Host(HostA), Vm(Vm1), Vm(Vm2)],
            [RunsOn(Vm1, HostA), RunsOn(Vm2, HostA)]);

        Assert.Equal(ComplianceVerdict.NotEvaluated, One(findings, ContinuityControls.DrsRule, "uuid-1").Verdict);
    }

    [Fact]
    public void A_cluster_with_no_rules_has_no_drs_finding_and_an_unread_one_is_not_evaluated()
    {
        Assert.DoesNotContain(Evaluate([DrsCluster()]), f => f.ControlId == ContinuityControls.DrsRule);

        var unread = One(Evaluate([Cluster()]), ContinuityControls.DrsRule);
        Assert.Equal(ComplianceVerdict.NotEvaluated, unread.Verdict);
    }

    // --- multipath --------------------------------------------------------------

    private static Entity Vmfs(string name, params string[] naas) => new()
    {
        Id = new EntityId($"vc-1:{name}"),
        Kind = EntityKind.Datastore,
        DisplayName = name,
        LastSeenUtc = T0,
        SourceInstanceId = "vc-1",
        Settings = new Dictionary<string, string> { ["type"] = "VMFS" },
        Marks = [.. naas.Select(n => IdentityMark.Create(IdentityMarkKind.StorageDeviceId, n, "vc-1"))],
    };

    private static StoragePath Path(
        string naa, string adapter, string? target = "50:00:00:00:00:00:00:01", string state = "active",
        string transport = "HostFibreChannelTargetTransport") => new()
        {
            Name = $"{adapter}:C0:T0:{naa}",
            StorageDeviceId = naa,
            Adapter = adapter,
            Target = target,
            State = state,
            Transport = transport,
        };

    private static readonly string[] ThirtyDevices = [.. Enumerable.Range(1, 30).Select(i => $"naa.{i:00}")];

    [Fact]
    public void One_hba_behind_thirty_shared_devices_is_one_finding_not_thirty()
    {
        StoragePath[] paths =
        [
            .. ThirtyDevices.SelectMany(n => new[]
            {
                Path(n, "vmhba1", "50:00:00:00:00:00:00:01"),
                Path(n, "vmhba1", "50:00:00:00:00:00:00:02"),
            }),
        ];

        var findings = Evaluate([Vmfs("ds", ThirtyDevices), Host(HostA, paths: paths), Host(HostB, paths: paths)]);

        var onA = findings.Where(f => f.Entity == HostA && f.ControlId == PathSingleHba).ToList();
        var hba = Assert.Single(onA);
        Assert.Equal("vmhba1", hba.Subject);
        Assert.Equal(ComplianceVerdict.Failing, hba.Verdict);
        Assert.Contains("30 shared device(s)", hba.Observed, StringComparison.Ordinal);

        // Two paths each: path-single passes for every device.
        Assert.Equal(30, findings.Count(f =>
            f.Entity == HostA && f.ControlId == PathSingle && f.Verdict == ComplianceVerdict.Passing));

        // The one HBA is the nearer cause; the ports are not judged on those devices.
        Assert.All(findings.Where(f => f.Entity == HostA && f.ControlId == PathSingleTarget),
            f => Assert.Equal(ComplianceVerdict.NotEvaluated, f.Verdict));
    }

    [Fact]
    public void A_single_path_fails_per_device_and_local_vmfs_is_never_judged()
    {
        var findings = Evaluate(
        [
            Vmfs("shared", "naa.shared"),
            Vmfs("local", "naa.local"),
            Host(HostA, paths: [Path("naa.shared", "vmhba1"), Path("naa.local", "vmhba0")]),
            Host(HostB, paths: [Path("naa.shared", "vmhba1"), Path("naa.shared", "vmhba2", "50:00:00:00:00:00:00:02")]),
        ]);

        var single = One([.. findings.Where(f => f.Entity == HostA)], PathSingle, "naa.shared");
        Assert.Equal(ComplianceVerdict.Failing, single.Verdict);
        Assert.Equal(ComplianceVerdict.Passing,
            One([.. findings.Where(f => f.Entity == HostB)], PathSingle, "naa.shared").Verdict);
        Assert.Contains("shared", single.SubjectLabel, StringComparison.Ordinal);

        Assert.DoesNotContain(findings, f => f.Subject == "naa.local");
    }

    [Fact]
    public void Two_hbas_and_two_ports_pass_both()
    {
        StoragePath[] paths =
        [
            Path("naa.1", "vmhba1", "50:00:00:00:00:00:00:01"),
            Path("naa.1", "vmhba2", "50:00:00:00:00:00:00:02"),
        ];

        var findings = Evaluate([Vmfs("ds", "naa.1"), Host(HostA, paths: paths), Host(HostB, paths: paths)])
            .Where(f => f.Entity == HostA)
            .ToList();

        Assert.Equal(ComplianceVerdict.Passing, One(findings, PathSingleHba, "vmhba1").Verdict);
        Assert.Equal(ComplianceVerdict.Passing, One(findings, PathSingleHba, "vmhba2").Verdict);
        Assert.Equal(ComplianceVerdict.Passing, One(findings, PathSingleTarget, "50:00:00:00:00:00:00:01").Verdict);
    }

    [Fact]
    public void Every_working_path_on_one_target_port_fails_that_port()
    {
        StoragePath[] paths =
        [
            Path("naa.1", "vmhba1", "50:00:00:00:00:00:00:01"),
            Path("naa.1", "vmhba2", "50:00:00:00:00:00:00:01"),
        ];

        var findings = Evaluate([Vmfs("ds", "naa.1"), Host(HostA, paths: paths), Host(HostB, paths: paths)])
            .Where(f => f.Entity == HostA)
            .ToList();

        Assert.Equal(ComplianceVerdict.Failing, One(findings, PathSingleTarget, "50:00:00:00:00:00:00:01").Verdict);
        Assert.Equal(ComplianceVerdict.Passing, One(findings, PathSingleHba, "vmhba1").Verdict);
    }

    [Fact]
    public void All_iscsi_and_dead_paths_are_not_judged_for_a_single_hba()
    {
        StoragePath[] iscsi =
        [
            Path("naa.1", "vmhba64", "iqn.a", transport: "HostInternetScsiTargetTransport"),
            Path("naa.1", "vmhba64", "iqn.b", transport: "HostInternetScsiTargetTransport"),
        ];

        var software = Evaluate([Vmfs("ds", "naa.1"), Host(HostA, paths: iscsi), Host(HostB, paths: iscsi)]);
        Assert.Equal(ComplianceVerdict.NotEvaluated,
            One(software.Where(f => f.Entity == HostA).ToList(), PathSingleHba, "vmhba64").Verdict);

        StoragePath[] dead =
        [
            Path("naa.1", "vmhba1"),
            Path("naa.1", "vmhba2", state: "dead"),
        ];

        var withDead = Evaluate([Vmfs("ds", "naa.1"), Host(HostA, paths: dead), Host(HostB, paths: dead)])
            .Where(f => f.Entity == HostA && f.ControlId == PathSingleHba)
            .ToList();

        Assert.DoesNotContain(withDead, f => f.Verdict == ComplianceVerdict.Failing);
    }

    [Fact]
    public void A_host_in_maintenance_keeps_its_subjects_but_is_not_judged()
    {
        StoragePath[] single = [Path("naa.1", "vmhba1")];

        var findings = Evaluate(
        [
            Vmfs("ds", "naa.1"),
            Host(HostA, ObservationState.InMaintenance, single),
            Host(HostB, paths: [Path("naa.1", "vmhba1"), Path("naa.1", "vmhba2")]),
        ]);

        var finding = Assert.Single(findings, f => f.Entity == HostA && f.ControlId == PathSingle);
        Assert.Equal("naa.1", finding.Subject);
        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("maintenance", finding.Reason, StringComparison.Ordinal);
    }

    // --- N+1 ----------------------------------------------------------------------

    private static readonly EntityId ClusterId = new("vc-1:domain-c1");

    private static DemandSnapshot Snapshot(
        double? cpu = 1.0,
        TimeSpan? history = null,
        TimeToFullResult? date = null,
        DateTimeOffset? taken = null,
        string? unreadable = null) => new()
        {
            TakenUtc = taken ?? T0,
            Clusters = new Dictionary<EntityId, ClusterDemand>
            {
                [ClusterId] = new ClusterDemand
                {
                    HostCount = 3,
                    Cpu = new ResourceDemand
                    {
                        DemandHosts = cpu,
                        AvailableAfterFailoverHosts = 1.8,
                        HistoryCovered = history ?? TimeSpan.FromDays(30),
                        HistoryEndUtc = T0,
                        Date = date,
                    },
                    Memory = new ResourceDemand { AvailableAfterFailoverHosts = 1.8 },
                    Unreadable = unreadable,
                },
            },
        };

    private static ComplianceFinding NPlusOne(DemandSnapshot? snapshot) =>
        One(Evaluate([Cluster()], demand: snapshot), NPlusOneCpu);

    [Fact]
    public void No_snapshot_an_old_one_or_an_unknown_cluster_is_not_evaluated()
    {
        Assert.Equal(ComplianceVerdict.NotEvaluated, NPlusOne(null).Verdict);
        Assert.Equal(ComplianceVerdict.NotEvaluated, NPlusOne(Snapshot(taken: T0.AddHours(-2))).Verdict);
        Assert.Equal(ComplianceVerdict.NotEvaluated,
            NPlusOne(Snapshot() with { Clusters = new Dictionary<EntityId, ClusterDemand>() }).Verdict);
        Assert.Equal(ComplianceVerdict.NotEvaluated, NPlusOne(Snapshot(unreadable: "TimeoutException")).Verdict);
        Assert.Equal(ComplianceVerdict.NotEvaluated, NPlusOne(Snapshot(cpu: null)).Verdict);
    }

    [Fact]
    public void Too_little_history_is_not_evaluated_never_passing()
    {
        var finding = NPlusOne(Snapshot(history: TimeSpan.FromDays(2)));

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("No 7 days of demand history", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Demand_over_the_headroom_fails_today_whatever_the_history()
    {
        Assert.Equal(ComplianceVerdict.Failing, NPlusOne(Snapshot(cpu: 2.1, history: TimeSpan.Zero)).Verdict);
    }

    [Fact]
    public void Enough_history_that_holds_passes_and_a_near_forecast_fails()
    {
        Assert.Equal(ComplianceVerdict.Passing, NPlusOne(Snapshot()).Verdict);

        var soon = new TimeToFullResult.Forecast
        {
            FullAtUtc = T0.AddDays(10),
            Days = 10,
            SlopePerDay = 0.05,
            Window = new TrendWindow(T0.AddDays(-30), T0),
            PValue = 0.001,
            PointsUsed = 700,
        };

        Assert.Equal(ComplianceVerdict.Failing, NPlusOne(Snapshot(date: soon)).Verdict);
    }

    [Fact]
    public void The_snapshot_is_taken_from_the_series_with_its_history_span()
    {
        var host1 = new EntityId("vc-1:host-1");
        var host2 = new EntityId("vc-1:host-2");

        var graph = EntityGraph.Empty with
        {
            Entities = new Dictionary<EntityId, Entity>
            {
                [ClusterId] = Cluster(),
                [host1] = Host(host1),
                [host2] = Host(host2),
            },
            Relationships =
            [
                new Relationship { From = host1, To = ClusterId, Kind = RelationshipKind.PartOf, ObservedAtUtc = T0 },
                new Relationship { From = host2, To = ClusterId, Kind = RelationshipKind.PartOf, ObservedAtUtc = T0 },
            ],
        };

        var snapshot = ContinuityDemand.Take(
            new TenDaySeries(), graph, T0, ClusterNPlusOnePolicy.Default, SeriesRetentionPolicy.Default,
            new ClusterNPlusOneCache());

        var demand = snapshot.Clusters[ClusterId];
        Assert.Equal(2, demand.HostCount);
        Assert.Equal(1.0, demand.Cpu.DemandHosts);
        Assert.Equal(TimeSpan.FromDays(10), demand.Cpu.HistoryCovered);
        Assert.Equal(T0, snapshot.TakenUtc);
    }

    /// <summary>50% on every host now, and a history point ten days ago and now.</summary>
    private sealed class TenDaySeries : ISeriesReader
    {
        public SeriesResult Query(SeriesQuery query) => new()
        {
            Key = query.Key,
            Resolution = query.Resolution ?? SeriesResolution.Raw,
            Exists = true,
            Points = query.MaxPoints == 1
                ? [Sample(T0)]
                : [Sample(T0.AddDays(-10)), Sample(T0)],
        };

        private static AggregatedSample Sample(DateTimeOffset at) =>
            new() { StartUtc = at, Min = 50, Max = 50, Sum = 50, Count = 1, Last = 50 };

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];
    }
}
