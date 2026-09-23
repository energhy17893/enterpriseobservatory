using System.Text.Json.Nodes;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Collectors.Simplivity.Tests;

/// <summary>Folding onto vSphere entities (ADR-0027) and what SimpliVity says about them.</summary>
public class SimplivityInventorySourceTests
{
    private static readonly DateTimeOffset Now = new FixedClock().UtcNow;

    private static EntityId Vc(string moRef) => EntityId.For(FakeOvc.Vcenter, moRef);

    /// <summary>The fixture estate: three hosts, one cluster, four VMs, all owned by vSphere.</summary>
    private static FakeDirectory Estate() =>
        FakeDirectory.For("host-21", "host-22", "host-23", "domain-c7", "vm-101", "vm-102", "vm-103", "vm-104");

    [Fact]
    public async Task Kibar_shape_26_hosts_fold_onto_vsphere_with_zero_new_entities()
    {
        // The vSphere side as its own collector leaves it: a vCenter marked with
        // its instanceUuid (PR #140) and 26 hosts.
        var vcenter = new Entity
        {
            Id = Vc("vcenter"),
            Kind = EntityKind.VCenter,
            DisplayName = "kbvc01.kibar.local",
            SourceInstanceId = FakeOvc.Vcenter,
            LastSeenUtc = Now,
            Marks = [IdentityMark.Create(IdentityMarkKind.HardwareUuid, FakeOvc.VcenterUuid, FakeOvc.Vcenter)],
        };
        var hosts = Enumerable.Range(1, 26).Select(i => new Entity
        {
            Id = Vc($"host-{i}"),
            Kind = EntityKind.EsxiHost,
            DisplayName = $"esx{i:00}.kibar.local",
            SourceInstanceId = FakeOvc.Vcenter,
            LastSeenUtc = Now,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Syslog.global.logHost"] = "x" },
        }).ToList();
        var graph = EntityGraph.Empty.Merge([vcenter, .. hosts], [], [FakeOvc.Vcenter], Now, EntityRetentionPolicy.Default);

        var ovc = new FakeOvc();
        ovc.Collections["hosts"] = FakeOvc.Hosts(26);
        var snapshot = await ovc.Source(new FakeDirectory(graph.Entities.Keys)).ReadAsync(CancellationToken.None);

        Assert.Empty(snapshot.Entities);
        Assert.Empty(snapshot.Failures);
        Assert.Equal(26, snapshot.Annotations.Count);

        var merged = graph.Merge(
            [], [], ["svt-kibar"], Now, EntityRetentionPolicy.Default,
            [.. snapshot.Annotations.Select(a => a with { SourceInstanceId = "svt-kibar", ReadAtUtc = Now })]);

        Assert.Equal(graph.Entities.Count, merged.Entities.Count);
        Assert.All(hosts, h =>
        {
            var after = merged.Entities[h.Id];
            Assert.Equal("ALIVE", after.Settings["simplivity.state"]);
            Assert.Equal("x", after.Settings["Syslog.global.logHost"]);
            Assert.Equal(FakeOvc.Vcenter, after.SourceInstanceId);
        });
    }

    [Fact]
    public async Task Every_page_is_read_by_offset_until_count()
    {
        var ovc = new FakeOvc();
        ovc.Collections["hosts"] = FakeOvc.Hosts(1_201);

        var snapshot = await ovc.Source(new FakeDirectory([])).ReadAsync(CancellationToken.None);

        Assert.Equal(1_201, snapshot.Failures.Count); // none fold: the directory is empty
        Assert.Contains("GET /api/hosts?limit=500&offset=0", ovc.Requests);
        Assert.Contains("GET /api/hosts?limit=500&offset=500", ovc.Requests);
        Assert.Contains("GET /api/hosts?limit=500&offset=1000", ovc.Requests);
        Assert.DoesNotContain("GET /api/hosts?limit=500&offset=1500", ovc.Requests);
    }

    [Fact]
    public async Task A_host_that_does_not_fold_is_a_named_failure_never_a_guess()
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.Collections["hosts"][0]!["hypervisor_object_id"] = "host-21"; // bare moRef: which vCenter?
        ovc.Collections["hosts"][1]!["hypervisor_object_id"] = "00000000-dead-beef-0000-000000000000:HostSystem:host-22";

        var snapshot = await ovc.Source(Estate()).ReadAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Failures.Count(f => f.Target.StartsWith("SimpliVity host", StringComparison.Ordinal)));
        Assert.All(snapshot.Failures, f =>
        {
            Assert.Equal(CollectionFailureKind.NotConfigured, f.Kind);
            Assert.Contains("could not fold", f.Detail, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(snapshot.Annotations, a => a.Entity == Vc("host-21") || a.Entity == Vc("host-22"));
        Assert.Contains(snapshot.Annotations, a => a.Entity == Vc("host-23"));
    }

    [Fact]
    public async Task A_faulty_host_is_a_critical_alert_on_the_vsphere_host()
    {
        var snapshot = await FakeOvc.FromFixtures().Source(Estate()).ReadAsync(CancellationToken.None);

        var alert = Assert.Single(snapshot.Alerts, a => a.Entity == Vc("host-23"));
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("svt-kibar", alert.Source);
    }

    [Theory]
    [InlineData("DEGRADED", AlertSeverity.Warning)]
    [InlineData("DEFUNCT", AlertSeverity.Critical)]
    [InlineData("SUSPECTED", AlertSeverity.Warning)]
    public async Task Ha_status_and_host_state_map_to_their_severity(string value, AlertSeverity expected)
    {
        var ovc = FakeOvc.FromFixtures();
        var field = value == "SUSPECTED" ? "hosts" : "virtual_machines";
        ovc.Collections[field][0]![field == "hosts" ? "state" : "ha_status"] = value;

        var snapshot = await ovc.Source(Estate()).ReadAsync(CancellationToken.None);

        var entity = field == "hosts" ? Vc("host-21") : Vc("vm-101");
        Assert.Equal(expected, Assert.Single(snapshot.Alerts, a => a.Entity == entity).Severity);
    }

    [Fact]
    public async Task Syncing_and_out_of_scope_are_data_not_alerts()
    {
        // Three-valued (ADR-0026), not Centreon's "anything but SAFE warns".
        var snapshot = await FakeOvc.FromFixtures().Source(Estate()).ReadAsync(CancellationToken.None);

        Assert.Equal("SYNCING", Settings(snapshot, Vc("vm-102"))["simplivity.ha_status"]);
        Assert.Equal("42", Settings(snapshot, Vc("vm-102"))["simplivity.ha_resynchronization_progress"]);
        Assert.Equal("OUT_OF_SCOPE", Settings(snapshot, Vc("vm-104"))["simplivity.ha_status"]);
        Assert.DoesNotContain(snapshot.Alerts, a => a.Entity == Vc("vm-102") || a.Entity == Vc("vm-104"));

        // The fixture's one DEGRADED VM is the one alert on a VM.
        Assert.Equal(Vc("vm-103"), Assert.Single(snapshot.Alerts, a => a.Title.Contains("HA", StringComparison.Ordinal)).Entity);
    }

    [Fact]
    public async Task A_lost_arbiter_is_an_alert_on_the_vsphere_cluster_and_upgrade_state_is_data_only()
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.Collections["omnistack_clusters"][0]!["arbiter_connected"] = false;

        var snapshot = await ovc.Source(Estate()).ReadAsync(CancellationToken.None);

        var cluster = Settings(snapshot, Vc("domain-c7"));
        Assert.Equal("false", cluster["simplivity.arbiter_connected"]);
        Assert.Equal("SUCCESS_MIXED_VERSION", cluster["simplivity.upgrade_state"]);
        Assert.Equal("3", cluster["simplivity.members"]);

        var alert = Assert.Single(snapshot.Alerts, a => a.Entity == Vc("domain-c7"));
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.DoesNotContain(snapshot.Alerts, a => a.Title.Contains("upgrade", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_cluster_without_its_own_composite_reference_folds_through_its_members()
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.Collections["omnistack_clusters"][0]!["hypervisor_object_id"] = "domain-c7";

        var snapshot = await ovc.Source(Estate()).ReadAsync(CancellationToken.None);

        Assert.Equal("SVT-Cluster-1", Settings(snapshot, Vc("domain-c7"))["simplivity.name"]);
    }

    [Fact]
    public async Task The_newest_protected_backup_is_what_a_vm_carries()
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.Collections["backups"].Add(JsonNode.Parse("""
            { "id": "b-old", "state": "PROTECTED", "type": "POLICY",
              "virtual_machine_id": "20a1b2c3-0000-4000-8000-000000000001", "created_at": "2026-09-01T02:00:00Z" }
            """)!);

        var snapshot = await ovc.Source(Estate()).ReadAsync(CancellationToken.None);

        Assert.Equal("2026-09-22T02:00:00.0000000Z", Settings(snapshot, Vc("vm-101"))[InventoryVerdictKeys.SimplivityBackupLastUtc]);
        Assert.Equal("POLICY", Settings(snapshot, Vc("vm-101"))["simplivity.backup.type"]);

        // app-vm-02's only backup FAILED: no date, so the freshness rule does
        // not hear from SimpliVity about it at all.
        Assert.False(Settings(snapshot, Vc("vm-102")).ContainsKey(InventoryVerdictKeys.SimplivityBackupLastUtc));
    }

    [Fact]
    public async Task A_vm_folds_by_instance_uuid_when_it_carries_no_composite_reference()
    {
        var ovc = FakeOvc.FromFixtures();
        var vm = ovc.Collections["virtual_machines"][0]!.AsObject();
        vm.Remove("hypervisor_object_id");
        vm["hypervisor_instance_id"] = "5012ABCD-0000-0000-0000-000000000101";
        var directory = Estate();
        directory.VirtualMachines["5012abcd-0000-0000-0000-000000000101"] = Vc("vm-101");

        var snapshot = await ovc.Source(directory).ReadAsync(CancellationToken.None);

        Assert.Equal("SAFE", Settings(snapshot, Vc("vm-101"))["simplivity.ha_status"]);
    }

    [Fact]
    public async Task A_deleted_vm_is_neither_folded_nor_a_failure()
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.Collections["virtual_machines"][0]!["state"] = "DELETED";

        var snapshot = await ovc.Source(FakeDirectory.For("host-21", "host-22", "host-23", "domain-c7", "vm-102", "vm-103", "vm-104"))
            .ReadAsync(CancellationToken.None);

        Assert.Empty(snapshot.Failures);
        Assert.DoesNotContain(snapshot.Annotations, a => a.Entity == Vc("vm-101"));
    }

    [Fact]
    public async Task Every_key_is_in_the_simplivity_namespace()
    {
        var snapshot = await FakeOvc.FromFixtures().Source(Estate()).ReadAsync(CancellationToken.None);

        Assert.Equal(8, snapshot.Annotations.Count);
        Assert.All(snapshot.Annotations, a =>
        {
            Assert.Equal("simplivity", a.Namespace);
            Assert.All(a.Settings.Keys, k => Assert.StartsWith("simplivity.", k, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task A_reply_without_the_collection_is_a_failure_not_an_empty_estate()
    {
        var ovc = FakeOvc.FromFixtures();
        ovc.RawBody = """{ "count": 0 }""";

        await Assert.ThrowsAsync<SimplivityApiException>(
            () => ovc.Source(Estate()).ReadAsync(CancellationToken.None));
    }

    private static IReadOnlyDictionary<string, string> Settings(InventorySnapshot snapshot, EntityId id) =>
        Assert.Single(snapshot.Annotations, a => a.Entity == id).Settings;
}
