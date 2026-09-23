using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.SimplivityControls;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The S2a SimpliVity rules as <c>eo-simplivity-1</c> checks. Each control:
/// Failing, Passing, NotEvaluated when the entity is SimpliVity but the value
/// was not read, and no finding at all when the entity carries no
/// <c>simplivity.*</c> annotation.
/// </summary>
public class SimplivityChecksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly ComplianceCatalogue Catalogue = SimplivityCatalogue.Build(SimplivityCatalogue.Production);

    private static readonly IReadOnlyDictionary<string, IComplianceCheck> ById =
        SimplivityCatalogue.ChecksById(SimplivityCatalogue.Production);

    private const string Svt = "simplivity.name";

    private static IReadOnlyList<ComplianceFinding> Evaluate(IReadOnlyList<Entity> estate, params Relationship[] edges)
    {
        var graph = EntityGraph.Empty with
        {
            Entities = estate.ToDictionary(e => e.Id, e => e),
            Relationships = edges,
        };

        return ComplianceEvaluation.Evaluate(Catalogue, estate, [], T0, checksById: ById, graph: graph);
    }

    private static ComplianceFinding One(IReadOnlyList<ComplianceFinding> findings, string control) =>
        Assert.Single(findings, f => f.ControlId == control);

    private static void None(IReadOnlyList<ComplianceFinding> findings, string control) =>
        Assert.DoesNotContain(findings, f => f.ControlId == control);

    private static Entity Make(string id, EntityKind kind, (string Key, string Value)[] settings, TimeConfiguration? time = null) => new()
    {
        Id = new EntityId(id),
        Kind = kind,
        DisplayName = id["vc-1:".Length..],
        LastSeenUtc = T0,
        SourceInstanceId = "vc-1",
        TimeConfiguration = time,
        Settings = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
    };

    private static Entity Cluster(params (string Key, string Value)[] settings) =>
        Make("vc-1:domain-c1", EntityKind.Cluster, settings);

    private static Entity SvtCluster(params (string Key, string Value)[] settings) =>
        Cluster([(Svt, "omni-1"), .. settings]);

    private static Entity Host(string name, TimeConfiguration? time = null, params (string Key, string Value)[] settings) =>
        Make($"vc-1:{name}", EntityKind.EsxiHost, settings, time);

    private static Entity Vm(string name, params (string Key, string Value)[] settings) =>
        Make($"vc-1:{name}", EntityKind.VirtualMachine, settings);

    private static Entity VCenter() => Make("vc-1:vcenter", EntityKind.VCenter, []);

    private static Relationship Edge(Entity from, Entity to, RelationshipKind kind) =>
        new() { From = from.Id, To = to.Id, Kind = kind, ObservedAtUtc = T0 };

    private static TimeConfiguration Ntp(params string[] servers) => new() { Protocol = "ntp", NtpServers = servers };

    // --- catalogue ------------------------------------------------------------

    [Fact]
    public void Production_registers_the_six_s2a_controls_each_with_a_source()
    {
        Assert.Equal(
            [DpmOff, AdmissionControlPolicy, VmSnapshots, MixedVersions, UpgradeCommitNeeded, NtpConsistent],
            Catalogue.Controls.Select(c => c.ControlId));
        Assert.All(Catalogue.Controls, c => Assert.False(string.IsNullOrWhiteSpace(c.Source)));
        Assert.Equal("eo-simplivity-1", Catalogue.Release);

        var descriptor = CatalogueDescriptor.Of(Catalogue);
        Assert.Equal(CatalogueDescriptor.SimplivityId, descriptor.Id);
        Assert.Equal(CatalogueOwner.Product, descriptor.Owner);
        Assert.Equal(CatalogueKind.Computed, descriptor.Kind);
    }

    [Fact]
    public void A_cluster_without_a_simplivity_annotation_gets_no_finding_at_all()
    {
        // DPM on, HA off, upgrade pending: every rule would fail -- but this is
        // not a SimpliVity cluster, so none of them applies.
        var findings = Evaluate(
        [
            Cluster(("dpmConfigInfo.enabled", "true"), ("dasConfig.enabled", "false"),
                ("dasConfig.admissionControlEnabled", "false")),
            VCenter(),
        ]);

        Assert.Empty(findings);
    }

    // --- svt.dpm-off -------------------------------------------------------------

    [Theory]
    [InlineData("true", ComplianceVerdict.Failing)]
    [InlineData("false", ComplianceVerdict.Passing)]
    public void Dpm_on_fails_and_off_passes(string enabled, ComplianceVerdict expected) =>
        Assert.Equal(expected, One(Evaluate([SvtCluster(("dpmConfigInfo.enabled", enabled))]), DpmOff).Verdict);

    [Fact]
    public void Dpm_not_read_is_not_evaluated() =>
        Assert.Equal(ComplianceVerdict.NotEvaluated, One(Evaluate([SvtCluster()]), DpmOff).Verdict);

    // --- svt.admission-control-policy ----------------------------------------------

    private static Entity AdmissionCluster(string policy) =>
        SvtCluster(("dasConfig.enabled", "true"), ("dasConfig.admissionControlEnabled", "true"),
            ("dasConfig.admissionControlPolicy.type", policy));

    [Theory]
    [InlineData("ClusterFailoverLevelAdmissionControlPolicy", "policy: slots (host failures to tolerate)")]
    [InlineData("ClusterFailoverHostAdmissionControlPolicy", "policy: dedicated failover host")]
    public void A_policy_that_is_not_a_resource_percentage_fails(string policy, string observed)
    {
        var finding = One(Evaluate([AdmissionCluster(policy)]), AdmissionControlPolicy);

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal(observed, finding.Observed);
    }

    [Fact]
    public void A_cluster_resource_percentage_policy_passes() =>
        Assert.Equal(
            ComplianceVerdict.Passing,
            One(Evaluate([AdmissionCluster("ClusterFailoverResourceAdmissionControlPolicy")]), AdmissionControlPolicy).Verdict);

    [Fact]
    public void Admission_control_off_is_left_to_the_continuity_control()
    {
        // The "enabled" half is eo-cont.ha-admission-control's finding (principle 4).
        var finding = One(
            Evaluate([SvtCluster(("dasConfig.enabled", "true"), ("dasConfig.admissionControlEnabled", "false"),
                ("dasConfig.admissionControlPolicy.type", "ClusterFailoverLevelAdmissionControlPolicy"))]),
            AdmissionControlPolicy);

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains(ContinuityControls.HaAdmissionControl, finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Ha_off_is_left_to_the_continuity_control()
    {
        var finding = One(
            Evaluate([SvtCluster(("dasConfig.enabled", "false"), ("dasConfig.admissionControlEnabled", "true"),
                ("dasConfig.admissionControlPolicy.type", "ClusterFailoverLevelAdmissionControlPolicy"))]),
            AdmissionControlPolicy);

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains(ContinuityControls.HaEnabled, finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_policy_not_read_is_not_evaluated() =>
        Assert.Equal(
            ComplianceVerdict.NotEvaluated,
            One(Evaluate([SvtCluster(("dasConfig.enabled", "true"), ("dasConfig.admissionControlEnabled", "true"))]),
                AdmissionControlPolicy).Verdict);

    [Fact]
    public void A_cluster_without_a_simplivity_annotation_gets_no_policy_finding() =>
        None(
            Evaluate([Cluster(("dasConfig.enabled", "true"), ("dasConfig.admissionControlEnabled", "true"),
                ("dasConfig.admissionControlPolicy.type", "ClusterFailoverLevelAdmissionControlPolicy"))]),
            AdmissionControlPolicy);

    // --- svt.vm-snapshots ----------------------------------------------------------

    private static IReadOnlyList<ComplianceFinding> SnapshotEstate(Entity cluster, params Entity[] vms)
    {
        var host = Host("host-1");
        return Evaluate(
            [cluster, host, .. vms],
            [Edge(host, cluster, RelationshipKind.PartOf), .. vms.Select(v => Edge(v, host, RelationshipKind.RunsOn))]);
    }

    [Fact]
    public void Snapshots_on_simplivity_vms_are_one_finding_on_the_cluster_with_a_count()
    {
        var findings = SnapshotEstate(
            Cluster(),
            Vm("vm-1", ("simplivity.ha_status", "SAFE"), ("snapshot.count", "2")),
            Vm("vm-2", ("simplivity.ha_status", "SAFE"), ("snapshot.count", "1")),
            Vm("vm-3", ("simplivity.ha_status", "SAFE"), ("snapshot.count", "0")),
            // Not a SimpliVity VM: its snapshots are the age alarm's business, not this.
            Vm("vm-4", ("snapshot.count", "5")));

        var finding = One(findings, VmSnapshots);

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal("vc-1:domain-c1", finding.Entity.Value);
        Assert.Equal(string.Empty, finding.Subject);
        Assert.StartsWith("2 VMs with 3 snapshots", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void No_snapshots_on_simplivity_vms_passes() =>
        Assert.Equal(
            ComplianceVerdict.Passing,
            One(SnapshotEstate(Cluster(), Vm("vm-1", ("simplivity.ha_status", "SAFE"), ("snapshot.count", "0"))), VmSnapshots).Verdict);

    [Fact]
    public void Simplivity_vms_whose_snapshot_tree_was_not_read_are_not_evaluated() =>
        Assert.Equal(
            ComplianceVerdict.NotEvaluated,
            One(SnapshotEstate(Cluster(), Vm("vm-1", ("simplivity.ha_status", "SAFE"))), VmSnapshots).Verdict);

    [Fact]
    public void A_cluster_with_no_simplivity_vm_gets_no_snapshot_finding() =>
        None(SnapshotEstate(Cluster(), Vm("vm-1", ("snapshot.count", "3"))), VmSnapshots);

    // --- svt.mixed-versions ----------------------------------------------------------

    private static Entity SvtHost(string name, string? version) =>
        version is null
            ? Host(name, null, ("simplivity.state", "ALIVE"))
            : Host(name, null, ("simplivity.state", "ALIVE"), ("simplivity.version", version));

    [Fact]
    public void Three_omnistack_versions_fail_on_the_vcenter()
    {
        var finding = One(
            Evaluate([VCenter(), SvtHost("h1", "5.1.0"), SvtHost("h2", "5.2.0"), SvtHost("h3", "5.3.0")]),
            MixedVersions);

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal("vc-1:vcenter", finding.Entity.Value);
        Assert.StartsWith("3 versions", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_omnistack_versions_pass() =>
        Assert.Equal(
            ComplianceVerdict.Passing,
            One(Evaluate([VCenter(), SvtHost("h1", "5.1.0"), SvtHost("h2", "5.2.0"), SvtHost("h3", "5.2.0")]), MixedVersions).Verdict);

    [Fact]
    public void A_simplivity_host_without_a_version_is_not_evaluated() =>
        Assert.Equal(
            ComplianceVerdict.NotEvaluated,
            One(Evaluate([VCenter(), SvtHost("h1", "5.1.0"), SvtHost("h2", null)]), MixedVersions).Verdict);

    [Fact]
    public void A_vcenter_with_no_simplivity_host_gets_no_version_finding() =>
        None(Evaluate([VCenter(), Host("h1")]), MixedVersions);

    // --- svt.upgrade-commit-needed ---------------------------------------------------

    [Theory]
    [InlineData("SUCCESS_COMMIT_NEEDED", ComplianceVerdict.Failing)]
    [InlineData("MIXED_VERSION", ComplianceVerdict.Failing)]
    [InlineData("SUCCESS_COMMITTED", ComplianceVerdict.Passing)]
    public void Upgrade_state_waiting_for_commit_fails(string state, ComplianceVerdict expected) =>
        Assert.Equal(
            expected,
            One(Evaluate([SvtCluster(("simplivity.upgrade_state", state))]), UpgradeCommitNeeded).Verdict);

    [Fact]
    public void Upgrade_state_not_read_is_not_evaluated() =>
        Assert.Equal(ComplianceVerdict.NotEvaluated, One(Evaluate([SvtCluster()]), UpgradeCommitNeeded).Verdict);

    // --- svt.ntp-consistent ------------------------------------------------------------

    private static IReadOnlyList<ComplianceFinding> NtpEstate(Entity cluster, params Entity[] hosts) =>
        Evaluate([cluster, .. hosts], [.. hosts.Select(h => Edge(h, cluster, RelationshipKind.PartOf))]);

    [Fact]
    public void Hosts_with_different_ntp_servers_fail_on_the_cluster()
    {
        var finding = One(
            NtpEstate(SvtCluster(), Host("h1", Ntp("ntp1", "ntp2")), Host("h2", Ntp("ntp3"))),
            NtpConsistent);

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal("vc-1:domain-c1", finding.Entity.Value);
    }

    [Fact]
    public void The_same_ntp_servers_in_another_order_or_case_pass() =>
        Assert.Equal(
            ComplianceVerdict.Passing,
            One(NtpEstate(SvtCluster(), Host("h1", Ntp("ntp1", "NTP2")), Host("h2", Ntp("ntp2", "ntp1"))), NtpConsistent).Verdict);

    [Fact]
    public void A_host_whose_time_configuration_was_not_read_is_not_evaluated() =>
        Assert.Equal(
            ComplianceVerdict.NotEvaluated,
            One(NtpEstate(SvtCluster(), Host("h1", Ntp("ntp1")), Host("h2")), NtpConsistent).Verdict);

    [Fact]
    public void A_cluster_without_a_simplivity_annotation_gets_no_ntp_finding() =>
        None(NtpEstate(Cluster(), Host("h1", Ntp("ntp1")), Host("h2", Ntp("ntp3"))), NtpConsistent);
}
