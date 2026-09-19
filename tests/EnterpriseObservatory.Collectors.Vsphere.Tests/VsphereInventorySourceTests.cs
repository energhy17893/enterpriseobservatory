using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Vsphere;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

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
    public async Task A_host_registered_by_address_is_marked_as_an_address()
    {
        // An address contains dots, so a naive FQDN test accepts it and then
        // derives a "short hostname" of "10" — a mark every host in the network
        // would share. Found against a live vCenter whose hosts are registered
        // by address.
        var snapshot = await Read(Payload(hosts:
            [Host(name: "10.5.1.76") with { Fqdn = null }]));

        var host = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost);

        Assert.Contains(host.Marks, m =>
            m.Kind == IdentityMarkKind.IpAddress && m.Value == "10.5.1.76");
        Assert.DoesNotContain(host.Marks, m => m.Kind == IdentityMarkKind.Fqdn);
        Assert.DoesNotContain(host.Marks, m =>
            m.Kind == IdentityMarkKind.ShortHostname && m.Value == "10");
    }

    [Fact]
    public async Task An_ipv6_address_is_also_recognised_as_an_address()
    {
        var snapshot = await Read(Payload(hosts:
            [Host(name: "2001:db8::1") with { Fqdn = null, IpAddresses = [] }]));

        var host = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost);

        Assert.Contains(host.Marks, m => m.Kind == IdentityMarkKind.IpAddress);
        Assert.DoesNotContain(host.Marks, m => m.Kind == IdentityMarkKind.ShortHostname);
    }

    [Fact]
    public async Task Entity_ids_are_qualified_by_vcenter()
    {
        // Managed object references are unique within a vCenter but not between
        // them; host-1 exists in every installation.
        var snapshot = await Read(Payload(hosts: [Host()]));

        var id = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost).Id.Value;

        Assert.StartsWith($"vc-1{EntityId.Separator}", id, StringComparison.Ordinal);

        // And never with a slash. An id ends up in a URL path, where a slash
        // silently becomes an extra segment — which made every entity page in
        // the product answer with the wrong thing.
        Assert.DoesNotContain('/', id);
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

    // --- datastore capacity -----------------------------------------------

    private static VsphereDatastore Store(
        string name, long? capacity, long? free, bool? accessible = true) => new()
    {
        MoRef = "ds-" + name,
        Name = name,
        CapacityBytes = capacity,
        FreeSpaceBytes = free,
        Accessible = accessible,
    };

    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public async Task A_datastore_nearly_full_raises_a_warning()
    {
        // The data for this was already being fetched. summary.capacity and
        // summary.freeSpace had been requested from vCenter on every cycle
        // since the collector was written, parsed, and then dropped — so the
        // most commonly configured alert in VMware monitoring was one the
        // product could not raise, for want of using what it already had.
        var snapshot = await Read(Payload(
            datastores: [Store("vmfs01", capacity: 100 * Gb, free: 12 * Gb)]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Title == "Datastore nearly full");

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("88", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_datastore_almost_out_of_space_is_critical()
    {
        // A VMFS datastore that fills completely does not degrade: every
        // machine with a snapshot or a thin disk on it stops, at once.
        var snapshot = await Read(Payload(
            datastores: [Store("vmfs01", capacity: 100 * Gb, free: 2 * Gb)]));

        Assert.Equal(
            AlertSeverity.Critical,
            Assert.Single(snapshot.Alerts, a => a.Title == "Datastore nearly full").Severity);
    }

    [Fact]
    public async Task A_datastore_with_room_raises_nothing()
    {
        var snapshot = await Read(Payload(
            datastores: [Store("vmfs01", capacity: 100 * Gb, free: 60 * Gb)]));

        Assert.DoesNotContain(snapshot.Alerts, a => a.Title == "Datastore nearly full");
    }

    [Fact]
    public async Task Both_severities_share_one_fingerprint()
    {
        // A datastore crossing from warning to critical should make the inbox
        // say the problem got worse, not that a second problem appeared
        // beside the first.
        var warning = await Read(Payload(datastores: [Store("vmfs01", 100 * Gb, 12 * Gb)]));
        var critical = await Read(Payload(datastores: [Store("vmfs01", 100 * Gb, 2 * Gb)]));

        Assert.Equal(
            Assert.Single(warning.Alerts, a => a.Title == "Datastore nearly full").Fingerprint,
            Assert.Single(critical.Alerts, a => a.Title == "Datastore nearly full").Fingerprint);
    }

    [Fact]
    public async Task The_message_carries_the_free_space_as_well_as_the_percentage()
    {
        // Ninety percent of a hundred-terabyte volume is ten terabytes free and
        // nobody's emergency; ninety percent of a five-hundred-gigabyte one is
        // somebody's weekend. A percentage alone cannot tell those apart and an
        // operator should not have to go and look.
        var snapshot = await Read(Payload(
            datastores: [Store("vmfs01", capacity: 100 * Gb, free: 10 * Gb)]));

        var description = Assert.Single(
            snapshot.Alerts, a => a.Title == "Datastore nearly full").Description;

        Assert.Contains("GB free", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_datastore_with_unreadable_capacity_is_not_called_full()
    {
        // Absent values mean the properties could not be read, and an
        // unreadable datastore is not a full one. Principle 1.
        var snapshot = await Read(Payload(datastores:
        [
            Store("nocapacity", capacity: null, free: 10 * Gb),
            Store("nofree", capacity: 100 * Gb, free: null),
        ]));

        Assert.DoesNotContain(snapshot.Alerts, a => a.Title == "Datastore nearly full");
    }

    [Fact]
    public async Task A_datastore_reporting_zero_capacity_is_not_called_full()
    {
        // Dividing by it would read as completely full, which is the most
        // alarming possible answer to arrive at by accident.
        var snapshot = await Read(Payload(
            datastores: [Store("vmfs01", capacity: 0, free: 0)]));

        Assert.DoesNotContain(snapshot.Alerts, a => a.Title == "Datastore nearly full");
    }

    [Fact]
    public async Task An_inaccessible_datastore_raises_an_alert_not_only_a_health_state()
    {
        // It was marked Critical and nothing reached the inbox — the same gap
        // the collector-unreachable alert exists to close, left open for the
        // one entity kind where it means an outage rather than a degradation.
        var snapshot = await Read(Payload(
            datastores: [Store("vmfs01", 100 * Gb, 50 * Gb, accessible: false)]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Title == "Datastore not accessible");

        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(Ds(snapshot, "vmfs01").Id, alert.Entity);
    }

    [Fact]
    public async Task An_unreadable_datastore_is_not_reported_as_inaccessible()
    {
        // Null is "we could not tell", which is not the same as "it is gone".
        var snapshot = await Read(Payload(
            datastores: [Store("vmfs01", 100 * Gb, 50 * Gb, accessible: null)]));

        Assert.DoesNotContain(snapshot.Alerts, a => a.Title == "Datastore not accessible");
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
