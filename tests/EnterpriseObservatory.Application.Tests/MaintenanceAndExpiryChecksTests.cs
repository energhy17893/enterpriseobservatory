using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// M8.4 (maintenance and vMotion blockers) and M8.7 (expiry radar) as
/// <c>eo-continuity-1</c> checks: one verdict per visible subject, Passing
/// included; unread input is not evaluated with its reason; a fix is a
/// Failing → Passing on the same row; a vanished entity leaves the evaluation.
/// </summary>
public class MaintenanceAndExpiryChecksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly ComplianceCatalogue Catalogue = ContinuityCatalogue.Build(ContinuityCatalogue.Production);

    private static readonly IReadOnlyDictionary<string, IComplianceCheck> ById =
        ContinuityCatalogue.ChecksById(ContinuityCatalogue.Production);

    private static IReadOnlyList<ComplianceFinding> Evaluate(
        IReadOnlyList<Entity> estate,
        IReadOnlyList<Relationship>? relationships = null,
        IReadOnlyList<ComplianceFinding>? previous = null)
    {
        var graph = EntityGraph.Empty with
        {
            Entities = estate.ToDictionary(e => e.Id, e => e),
            Relationships = relationships ?? [],
        };

        return ComplianceEvaluation.Evaluate(
            Catalogue, estate, previous ?? [], T0, checksById: ById, graph: graph);
    }

    private static ComplianceFinding One(IReadOnlyList<ComplianceFinding> findings, string control, Entity entity) =>
        Assert.Single(findings, f => f.ControlId == control && f.Entity == entity.Id && f.Subject.Length == 0);

    private static Entity Make(EntityKind kind, string id, params (string Key, string Value)[] settings) => new()
    {
        Id = new EntityId(id),
        Kind = kind,
        DisplayName = id,
        LastSeenUtc = T0,
        SourceInstanceId = "vc-1",
        Settings = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
    };

    private static Entity Vm(string id, params (string Key, string Value)[] settings) =>
        Make(EntityKind.VirtualMachine, id, settings);

    private static Relationship Edge(Entity from, Entity to, RelationshipKind kind) =>
        new() { From = from.Id, To = to.Id, Kind = kind, ObservedAtUtc = T0 };

    // --- catalogue --------------------------------------------------------------

    [Fact]
    public void The_maintenance_and_expiry_controls_are_registered_each_with_a_source()
    {
        string[] expected =
        [
            "eo-cont.maint-cdrom", "eo-cont.maint-consolidation", "eo-cont.maint-single-host-datastore",
            "eo-cont.maint-evc", "eo-cont.cert-esxi", "eo-cont.cert-vcenter",
        ];

        var ids = Catalogue.Controls.Select(c => c.ControlId).ToList();

        Assert.All(expected, id => Assert.Contains(id, ids));
        Assert.All(Catalogue.Controls.Where(c => expected.Contains(c.ControlId)),
            c => Assert.False(string.IsNullOrWhiteSpace(c.Source)));
        Assert.Equal("Tool default (vCheck 60 days)", Catalogue.Controls.Single(c => c.ControlId == CertEsxi).Source);
        Assert.Equal("Tool default (vCheck 60 days)", Catalogue.Controls.Single(c => c.ControlId == CertVCenter).Source);
    }

    // --- connected CD / ISO ----------------------------------------------------

    [Fact]
    public void A_connected_cd_fails_on_its_vm_and_says_how_many_are_iso_backed()
    {
        var vm = Vm("vc-1:vm-1", (InventoryVerdictKeys.ConnectedCdroms, "2"), (InventoryVerdictKeys.ConnectedIsoCdroms, "1"));

        var finding = One(Evaluate([vm]), MaintCdrom, vm);

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Contains("2 CD/DVD drives connected", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("1 backed by an ISO file", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Disconnecting_the_cd_passes_on_the_same_row()
    {
        var failing = Evaluate([Vm("vc-1:vm-1", (InventoryVerdictKeys.ConnectedCdroms, "1"), (InventoryVerdictKeys.ConnectedIsoCdroms, "1"))]);
        var vm = Vm("vc-1:vm-1", (InventoryVerdictKeys.ConnectedCdroms, "0"), (InventoryVerdictKeys.ConnectedIsoCdroms, "0"));

        var finding = One(Evaluate([vm], previous: failing), MaintCdrom, vm);

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Equal(T0, finding.FirstSeenUtc);
    }

    [Fact]
    public void A_vm_whose_devices_were_not_read_is_not_evaluated()
    {
        var vm = Vm("vc-1:vm-1");

        var finding = One(Evaluate([vm]), MaintCdrom, vm);

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("config.hardware.device", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vanished_vm_leaves_the_evaluation()
    {
        var vm = Vm("vc-1:vm-1", (InventoryVerdictKeys.ConnectedCdroms, "1")) with
        {
            ObservationState = ObservationState.Vanished,
        };

        Assert.DoesNotContain(Evaluate([vm]), f => f.Entity == vm.Id);
    }

    // --- consolidation ---------------------------------------------------------

    [Theory]
    [InlineData("true", ComplianceVerdict.Failing)]
    [InlineData("false", ComplianceVerdict.Passing)]
    [InlineData("sometimes", ComplianceVerdict.NotEvaluated)]
    public void Consolidation_is_vcenters_own_verdict(string value, ComplianceVerdict expected)
    {
        var vm = Vm("vc-1:vm-1", (InventoryVerdictKeys.ConsolidationNeeded, value));

        Assert.Equal(expected, One(Evaluate([vm]), MaintConsolidation, vm).Verdict);
    }

    [Fact]
    public void Consolidating_passes_on_the_same_row_and_unread_is_not_evaluated()
    {
        var before = Evaluate([Vm("vc-1:vm-1", (InventoryVerdictKeys.ConsolidationNeeded, "true"))]);
        var fixedVm = Vm("vc-1:vm-1", (InventoryVerdictKeys.ConsolidationNeeded, "false"));
        var unread = Vm("vc-1:vm-2");

        var after = Evaluate([fixedVm, unread], previous: before);

        Assert.Equal(ComplianceVerdict.Passing, One(after, MaintConsolidation, fixedVm).Verdict);
        var notRead = One(after, MaintConsolidation, unread);
        Assert.Equal(ComplianceVerdict.NotEvaluated, notRead.Verdict);
        Assert.Contains("consolidationNeeded", notRead.Reason, StringComparison.Ordinal);
    }

    // --- single-host datastore -------------------------------------------------

    private static readonly Entity HostA = Make(EntityKind.EsxiHost, "vc-1:host-a");

    private static Entity Datastore(string mountedHosts) =>
        Make(EntityKind.Datastore, "vc-1:ds-local", (InventoryVerdictKeys.MountedHostCount, mountedHosts));

    private static Entity PoweredVm(string id, string power = "poweredOn") => Vm(id, ("powerState", power));

    private static (IReadOnlyList<Entity>, IReadOnlyList<Relationship>) Pinned(Entity datastore, params Entity[] vms) =>
        (
            [datastore, HostA, .. vms],
            [.. vms.SelectMany(vm => new[] { Edge(vm, datastore, RelationshipKind.BackedBy), Edge(vm, HostA, RelationshipKind.RunsOn) })]
        );

    [Fact]
    public void A_single_host_datastore_carrying_running_vms_fails_and_names_them()
    {
        var ds = Datastore("1");
        var (estate, edges) = Pinned(ds, PoweredVm("vc-1:app01"), PoweredVm("vc-1:off01", "poweredOff"),
            PoweredVm("vc-1:vm-agent") with { DisplayName = "vCLS-1234" });

        var finding = One(Evaluate(estate, edges), MaintSingleHostDatastore, ds);

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Contains("1 running virtual machine", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("vc-1:app01", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("vc-1:host-a", finding.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain("off01", finding.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain("vCLS", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_host_datastore_with_nothing_running_passes()
    {
        var ds = Datastore("1");
        var (estate, edges) = Pinned(ds, PoweredVm("vc-1:off01", "poweredOff"));

        Assert.Equal(ComplianceVerdict.Passing, One(Evaluate(estate, edges), MaintSingleHostDatastore, ds).Verdict);
    }

    [Theory]
    [InlineData("1", ComplianceVerdict.Failing)]
    [InlineData("2", ComplianceVerdict.Passing)]
    public void Two_mounting_hosts_is_the_boundary(string mounted, ComplianceVerdict expected)
    {
        var ds = Datastore(mounted);
        var (estate, edges) = Pinned(ds, PoweredVm("vc-1:app01"));

        Assert.Equal(expected, One(Evaluate(estate, edges), MaintSingleHostDatastore, ds).Verdict);
    }

    [Fact]
    public void Mounting_it_on_a_second_host_passes_on_the_same_row_and_unread_mounts_are_not_evaluated()
    {
        var (estate, edges) = Pinned(Datastore("1"), PoweredVm("vc-1:app01"));
        var before = Evaluate(estate, edges);

        var ds = Datastore("2");
        var (fixedEstate, fixedEdges) = Pinned(ds, PoweredVm("vc-1:app01"));
        var after = Evaluate(fixedEstate, fixedEdges, before);

        Assert.Equal(ComplianceVerdict.Passing, One(after, MaintSingleHostDatastore, ds).Verdict);

        var unread = Make(EntityKind.Datastore, "vc-1:ds-unread");
        var notRead = One(Evaluate([unread]), MaintSingleHostDatastore, unread);
        Assert.Equal(ComplianceVerdict.NotEvaluated, notRead.Verdict);
        Assert.Contains("host", notRead.Reason, StringComparison.Ordinal);
    }

    // --- EVC ---------------------------------------------------------------------

    private static Entity Cluster(string id, bool? evc, string? mode = null)
    {
        var settings = new List<(string, string)>();
        if (evc is { } on)
        {
            settings.Add((InventoryVerdictKeys.EvcEnabled, on ? "true" : "false"));
        }

        if (mode is not null)
        {
            settings.Add((InventoryVerdictKeys.EvcModeKey, mode));
        }

        return Make(EntityKind.Cluster, id, [.. settings]);
    }

    private static Entity EvcHost(string id, string? maxMode) =>
        maxMode is null
            ? Make(EntityKind.EsxiHost, id)
            : Make(EntityKind.EsxiHost, id, (InventoryVerdictKeys.HostMaxEvcModeKey, maxMode));

    private static ComplianceFinding EvcOf(Entity cluster, params Entity[] hosts) =>
        One(Evaluate([cluster, .. hosts], [.. hosts.Select(h => Edge(h, cluster, RelationshipKind.PartOf))]),
            MaintEvc, cluster);

    [Fact]
    public void Evc_on_passes_and_an_unread_cluster_summary_is_not_evaluated()
    {
        var on = EvcOf(Cluster("vc-1:c-on", evc: true, mode: "intel-cascadelake"),
            EvcHost("vc-1:h1", "intel-icelake"), EvcHost("vc-1:h2", "intel-cascadelake"));
        Assert.Equal(ComplianceVerdict.Passing, on.Verdict);
        Assert.Contains("intel-cascadelake", on.Observed, StringComparison.Ordinal);

        var unread = EvcOf(Cluster("vc-1:c-unread", evc: null), EvcHost("vc-1:h3", "intel-icelake"));
        Assert.Equal(ComplianceVerdict.NotEvaluated, unread.Verdict);
    }

    [Fact]
    public void Evc_off_with_every_host_the_same_cpu_generation_passes_and_says_why()
    {
        var finding = EvcOf(Cluster("vc-1:c-off", evc: false),
            EvcHost("vc-1:h1", "intel-icelake"), EvcHost("vc-1:h2", "intel-icelake"));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("EVC off", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("all 2 hosts", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("intel-icelake", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Evc_off_with_hosts_of_different_cpu_generations_fails_and_names_them()
    {
        var finding = EvcOf(Cluster("vc-1:c-mixed", evc: false),
            EvcHost("vc-1:h1", "intel-icelake"), EvcHost("vc-1:h2", "intel-icelake"),
            EvcHost("vc-1:h3", "intel-cascadelake"));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Contains("intel-cascadelake: vc-1:h3", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("intel-icelake: vc-1:h1, vc-1:h2", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Evc_off_with_a_host_unread_is_not_evaluated_unless_the_read_ones_already_differ()
    {
        var unknown = EvcOf(Cluster("vc-1:c-part", evc: false),
            EvcHost("vc-1:h1", "intel-icelake"), EvcHost("vc-1:h2", maxMode: null));
        Assert.Equal(ComplianceVerdict.NotEvaluated, unknown.Verdict);
        Assert.Contains("maxEVCModeKey", unknown.Reason, StringComparison.Ordinal);
        Assert.Contains("vc-1:h2", unknown.Reason, StringComparison.Ordinal);

        var differs = EvcOf(Cluster("vc-1:c-part2", evc: false),
            EvcHost("vc-1:h4", "intel-icelake"), EvcHost("vc-1:h5", "amd-zen3"), EvcHost("vc-1:h6", maxMode: null));
        Assert.Equal(ComplianceVerdict.Failing, differs.Verdict);
    }

    [Fact]
    public void Evc_off_on_a_cluster_of_one_host_or_none_passes_and_a_vanished_host_is_left_out()
    {
        var single = EvcOf(Cluster("vc-1:c-one", evc: false), EvcHost("vc-1:h1", "intel-icelake"));
        Assert.Equal(ComplianceVerdict.Passing, single.Verdict);

        var empty = EvcOf(Cluster("vc-1:c-empty", evc: false));
        Assert.Equal(ComplianceVerdict.Passing, empty.Verdict);

        var gone = EvcHost("vc-1:h9", "amd-zen3") with { ObservationState = ObservationState.Vanished };
        var withGone = EvcOf(Cluster("vc-1:c-gone", evc: false),
            EvcHost("vc-1:h7", "intel-icelake"), EvcHost("vc-1:h8", "intel-icelake"), gone);
        Assert.Equal(ComplianceVerdict.Passing, withGone.Verdict);
    }

    // --- certificates --------------------------------------------------------------

    private static Entity WithCertificate(EntityKind kind, string id, DateTimeOffset? notAfter, string sha256 = "AB12") =>
        notAfter is { } date
            ? Make(kind, id,
                (InventoryVerdictKeys.CertificateNotAfter, date.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                (InventoryVerdictKeys.CertificateSha256, sha256))
            : Make(kind, id);

    public static TheoryData<EntityKind, string> CertificateControls => new()
    {
        { EntityKind.EsxiHost, CertEsxi },
        { EntityKind.VCenter, CertVCenter },
    };

    [Theory]
    [MemberData(nameof(CertificateControls))]
    public void A_certificate_is_judged_at_sixty_days_and_on_its_expiry(EntityKind kind, string control)
    {
        var expired = WithCertificate(kind, "vc-1:expired", T0.AddDays(-3));
        var atNow = WithCertificate(kind, "vc-1:now", T0);
        var atThreshold = WithCertificate(kind, "vc-1:sixty", T0.AddDays(60));
        var beyond = WithCertificate(kind, "vc-1:beyond", T0.AddDays(60).AddMinutes(1));

        var findings = Evaluate([expired, atNow, atThreshold, beyond]);

        var gone = One(findings, control, expired);
        Assert.Equal(ComplianceVerdict.Failing, gone.Verdict);
        Assert.StartsWith("expired", gone.Observed, StringComparison.Ordinal);
        Assert.StartsWith("expired", One(findings, control, atNow).Observed, StringComparison.Ordinal);

        var soon = One(findings, control, atThreshold);
        Assert.Equal(ComplianceVerdict.Failing, soon.Verdict);
        Assert.StartsWith("expires in 60 days", soon.Observed, StringComparison.Ordinal);
        Assert.Contains("AB12", soon.Observed, StringComparison.Ordinal);

        Assert.Equal(ComplianceVerdict.Passing, One(findings, control, beyond).Verdict);
    }

    [Theory]
    [MemberData(nameof(CertificateControls))]
    public void Renewing_the_certificate_passes_on_the_same_row(EntityKind kind, string control)
    {
        var before = Evaluate([WithCertificate(kind, "vc-1:x", T0.AddDays(10), "OLD")]);
        var renewed = WithCertificate(kind, "vc-1:x", T0.AddDays(365), "NEW");

        var finding = One(Evaluate([renewed], previous: before), control, renewed);

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("NEW", finding.Observed, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CertificateControls))]
    public void An_unread_or_unparseable_certificate_is_not_evaluated(EntityKind kind, string control)
    {
        var unread = WithCertificate(kind, "vc-1:unread", null);
        var garbled = Make(kind, "vc-1:garbled", (InventoryVerdictKeys.CertificateNotAfter, "not a date"));

        var findings = Evaluate([unread, garbled]);

        Assert.All([unread, garbled], entity =>
        {
            var finding = One(findings, control, entity);
            Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
            Assert.Contains("certificate", finding.Reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void A_vanished_host_leaves_the_certificate_evaluation()
    {
        var host = WithCertificate(EntityKind.EsxiHost, "vc-1:host-x", T0.AddDays(1)) with
        {
            ObservationState = ObservationState.Vanished,
        };

        Assert.DoesNotContain(Evaluate([host]), f => f.ControlId == CertEsxi);
    }
}
