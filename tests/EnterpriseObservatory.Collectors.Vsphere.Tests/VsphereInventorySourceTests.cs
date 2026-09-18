using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

public class VsphereInventorySourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }

    private sealed class FakeApi(VsphereInventoryPayload payload) : IVsphereInventoryApi
    {
        public string InstanceId => "vc-1";

        public Task<VsphereInventoryPayload> RetrieveInventoryAsync(CancellationToken ct) =>
            Task.FromResult(payload);
    }

    private static Task<InventorySnapshot> Read(VsphereInventoryPayload payload) =>
        new VsphereInventorySource(new FakeApi(payload), new FixedClock())
            .ReadAsync(CancellationToken.None);

    private static VsphereInventoryPayload Payload(
        IReadOnlyList<VsphereHost>? hosts = null,
        IReadOnlyList<VsphereVirtualMachine>? vms = null,
        IReadOnlyList<VsphereCluster>? clusters = null,
        IReadOnlyList<VsphereDatastore>? datastores = null,
        IReadOnlyList<VsphereReadFailure>? failures = null) => new()
        {
            VCenterName = "vc01.corp.local",
            Hosts = hosts ?? [],
            VirtualMachines = vms ?? [],
            Clusters = clusters ?? [],
            Datastores = datastores ?? [],
            Failures = failures ?? [],
        };

    private static VsphereHost Host(
        string moRef = "host-1",
        string name = "esx01.corp.local",
        string? connectionState = "connected",
        string? status = "green",
        bool maintenance = false,
        string? cluster = null) => new()
        {
            MoRef = moRef,
            Name = name,
            HardwareUuid = "4c4c4544-0032-1234",
            IpAddresses = ["10.5.1.10"],
            Fqdn = name,
            ConnectionState = connectionState,
            OverallStatus = status,
            InMaintenanceMode = maintenance,
            ClusterMoRef = cluster,
        };

    // --- identity ---------------------------------------------------------

    [Fact]
    public async Task A_host_reports_the_marks_that_let_it_be_matched_later()
    {
        var snapshot = await Read(Payload(hosts: [Host()]));

        var host = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost);

        Assert.Contains(host.Marks, m => m.Kind == IdentityMarkKind.HardwareUuid);
        Assert.Contains(host.Marks, m => m.Kind == IdentityMarkKind.IpAddress);
        Assert.Contains(host.Marks, m => m.Kind == IdentityMarkKind.Fqdn);
        Assert.Contains(host.Marks, m => m.Kind == IdentityMarkKind.ShortHostname && m.Value == "esx01");
    }

    [Fact]
    public async Task A_host_added_by_bare_address_still_reports_a_usable_mark()
    {
        // vCenter names a host by whatever it was added as.
        var snapshot = await Read(Payload(hosts:
            [Host(name: "esx02", cluster: null) with { Fqdn = null }]));

        var host = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost);

        Assert.Contains(host.Marks, m => m.Kind == IdentityMarkKind.ShortHostname && m.Value == "esx02");
        Assert.DoesNotContain(host.Marks, m => m.Kind == IdentityMarkKind.Fqdn);
    }

    [Fact]
    public async Task Entity_ids_are_qualified_by_vcenter()
    {
        // Managed object references are unique within a vCenter but not between
        // them; host-1 exists in every installation.
        var snapshot = await Read(Payload(hosts: [Host()]));

        Assert.StartsWith("vc-1/", snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost).Id.Value,
            StringComparison.Ordinal);
    }

    // --- health honesty ---------------------------------------------------

    [Fact]
    public async Task A_disconnected_host_is_unknown_and_alerts_rather_than_keeping_its_last_colour()
    {
        var snapshot = await Read(Payload(hosts:
            [Host(connectionState: "notResponding", status: "green")]));

        var host = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost);
        Assert.Equal(HealthState.Unknown, host.Health);

        var alert = Assert.Single(snapshot.Alerts, a => a.Title == "Host not reachable from vCenter");
        Assert.Equal(Domain.Alerts.AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public async Task A_host_in_maintenance_keeps_its_health_but_is_marked_as_such()
    {
        // Maintenance is not a health state; it is why alerting should be
        // suppressed.
        var snapshot = await Read(Payload(hosts: [Host(maintenance: true, status: "yellow")]));

        var host = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost);

        Assert.Equal(ObservationState.InMaintenance, host.ObservationState);
        Assert.Equal(HealthState.Warning, host.Health);
    }

    [Theory]
    [InlineData("green", HealthState.Healthy)]
    [InlineData("yellow", HealthState.Warning)]
    [InlineData("red", HealthState.Critical)]
    [InlineData("gray", HealthState.Unknown)]
    [InlineData("chartreuse", HealthState.Unknown)]
    [InlineData(null, HealthState.Unknown)]
    public async Task An_unfamiliar_health_colour_is_unknown_rather_than_healthy(
        string? colour, HealthState expected)
    {
        // Defaulting an unrecognised value to healthy is how a monitoring
        // product ends up green during an outage.
        var snapshot = await Read(Payload(hosts: [Host(status: colour)]));

        Assert.Equal(expected, snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost).Health);
    }

    [Fact]
    public async Task A_powered_off_vm_is_unknown_rather_than_healthy()
    {
        // It is not unhealthy, but neither is it observed; reporting it healthy
        // would claim something we did not measure.
        var snapshot = await Read(Payload(
            hosts: [Host()],
            vms: [new VsphereVirtualMachine
            {
                MoRef = "vm-1", Name = "db01", PowerState = "poweredOff",
                OverallStatus = "green", HostMoRef = "host-1",
            }]));

        Assert.Equal(HealthState.Unknown,
            snapshot.Entities.Single(e => e.Kind == EntityKind.VirtualMachine).Health);
    }

    [Fact]
    public async Task An_unreadable_datastore_accessibility_is_unknown_not_accessible()
    {
        var snapshot = await Read(Payload(datastores:
        [
            new VsphereDatastore { MoRef = "ds-1", Name = "vmfs01", Accessible = null },
            new VsphereDatastore { MoRef = "ds-2", Name = "vmfs02", Accessible = false },
            new VsphereDatastore { MoRef = "ds-3", Name = "vmfs03", Accessible = true },
        ]));

        Assert.Equal(HealthState.Unknown, Ds(snapshot, "vmfs01").Health);
        Assert.Equal(HealthState.Critical, Ds(snapshot, "vmfs02").Health);
        Assert.Equal(HealthState.Healthy, Ds(snapshot, "vmfs03").Health);
    }

    // --- cluster configuration --------------------------------------------

    [Fact]
    public async Task An_unreadable_cluster_configuration_is_reported_not_assumed_off()
    {
        // "We could not read the HA setting" and "HA is off" lead to opposite
        // actions. The previous product got this right and it is kept.
        var snapshot = await Read(Payload(clusters:
            [new VsphereCluster { MoRef = "c-1", Name = "prod", HighAvailabilityEnabled = null, DrsEnabled = null }]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Title == "Cluster configuration unreadable");
        Assert.Contains("Unknown", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_readable_cluster_configuration_raises_nothing()
    {
        var snapshot = await Read(Payload(clusters:
            [new VsphereCluster { MoRef = "c-1", Name = "prod", HighAvailabilityEnabled = true, DrsEnabled = true }]));

        Assert.Empty(snapshot.Alerts);
    }

    // --- relationships ----------------------------------------------------

    [Fact]
    public async Task The_topology_is_expressed_as_typed_edges()
    {
        var snapshot = await Read(Payload(
            hosts: [Host(cluster: "c-1")],
            vms: [new VsphereVirtualMachine
            {
                MoRef = "vm-1", Name = "db01", PowerState = "poweredOn",
                HostMoRef = "host-1", DatastoreMoRefs = ["ds-1"],
            }],
            clusters: [new VsphereCluster { MoRef = "c-1", Name = "prod", HighAvailabilityEnabled = true, DrsEnabled = true }],
            datastores: [new VsphereDatastore { MoRef = "ds-1", Name = "vmfs01", Accessible = true }]));

        Assert.Contains(snapshot.Relationships, r => r.Kind == RelationshipKind.PartOf);
        Assert.Contains(snapshot.Relationships, r => r.Kind == RelationshipKind.RunsOn);
        Assert.Contains(snapshot.Relationships, r => r.Kind == RelationshipKind.BackedBy);
        Assert.Contains(snapshot.Relationships, r => r.Kind == RelationshipKind.ManagedBy);
    }

    [Fact]
    public async Task An_edge_is_never_created_to_something_we_did_not_see()
    {
        // A reference to a host in another vCenter, or one we failed to read,
        // would otherwise become an edge pointing at nothing.
        var snapshot = await Read(Payload(
            vms: [new VsphereVirtualMachine
            {
                MoRef = "vm-1", Name = "db01", PowerState = "poweredOn",
                HostMoRef = "host-999", DatastoreMoRefs = ["ds-999"],
            }]));

        Assert.DoesNotContain(snapshot.Relationships, r => r.Kind == RelationshipKind.RunsOn);
        Assert.DoesNotContain(snapshot.Relationships, r => r.Kind == RelationshipKind.BackedBy);
        // The VM itself is still reported; only the unfounded edges are dropped.
        Assert.Contains(snapshot.Entities, e => e.Kind == EntityKind.VirtualMachine);
    }

    [Fact]
    public async Task Everything_is_attributed_to_the_vcenter_that_manages_it()
    {
        // ManagedBy is what lets the management plane outrank a BMC log later.
        var snapshot = await Read(Payload(
            hosts: [Host()],
            datastores: [new VsphereDatastore { MoRef = "ds-1", Name = "vmfs01", Accessible = true }]));

        var vCenter = snapshot.Entities.Single(e => e.Kind == EntityKind.VCenter);

        Assert.Equal(2, snapshot.Relationships.Count(r =>
            r.Kind == RelationshipKind.ManagedBy && r.To == vCenter.Id));
    }

    // --- failures ---------------------------------------------------------

    [Fact]
    public async Task A_permission_failure_is_classified_as_such_rather_than_as_a_protocol_error()
    {
        // They lead to different fixes: one is a role change, the other a bug
        // report.
        var snapshot = await Read(Payload(failures:
        [
            new VsphereReadFailure { Target = "cluster config", Detail = "NoPermission", IsPermissionDenied = true },
            new VsphereReadFailure { Target = "datastore summary", Detail = "malformed response" },
        ]));

        Assert.Contains(snapshot.Failures, f => f.Kind == CollectionFailureKind.AuthorizationDenied);
        Assert.Contains(snapshot.Failures, f => f.Kind == CollectionFailureKind.ProtocolError);
    }

    [Fact]
    public async Task An_empty_vcenter_still_reports_itself()
    {
        // An installation with nothing in it is a real state, not an error, and
        // the vCenter entity is what carries "we looked and found nothing".
        var snapshot = await Read(Payload());

        var entity = Assert.Single(snapshot.Entities);
        Assert.Equal(EntityKind.VCenter, entity.Kind);
        Assert.Empty(snapshot.Failures);
    }

    private static Entity Ds(InventorySnapshot snapshot, string name) =>
        snapshot.Entities.Single(e => e.Kind == EntityKind.Datastore && e.DisplayName == name);
}
