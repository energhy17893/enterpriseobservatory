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
        IReadOnlyList<VsphereTriggeredAlarm>? alarms = null,
        IReadOnlyList<VsphereReadFailure>? failures = null) => new()
        {
            VCenterName = "vc01.corp.local",
            Hosts = hosts ?? [],
            VirtualMachines = vms ?? [],
            Clusters = clusters ?? [],
            Datastores = datastores ?? [],
            TriggeredAlarms = alarms ?? [],
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

    // --- capacity as a series (M4.1) -------------------------------------

    private static Dictionary<string, double> Readings(InventorySnapshot snapshot, string datastore) =>
        snapshot.Observations
            .Where(o => o.Entity == EntityId.For("vc-1", "ds-" + datastore))
            .ToDictionary(o => o.Value.CounterName, o => o.Value.Raw);

    [Fact]
    public async Task Capacity_is_kept_as_five_series_on_the_datastore()
    {
        // The same three properties the fullness and over-commit alerts are
        // computed from, plus the two figures derived from them. No extra
        // call to vCenter: before this they were used once and dropped.
        var snapshot = await Read(Payload(datastores:
        [
            Store("vmfs01", capacity: 100 * Gb, free: 30 * Gb) with { UncommittedBytes = 50 * Gb },
        ]));

        var readings = Readings(snapshot, "vmfs01");

        Assert.Equal(100d * Gb, readings[CapacityCounters.DatastoreCapacity]);
        Assert.Equal(30d * Gb, readings[CapacityCounters.DatastoreFree]);
        Assert.Equal(70d * Gb, readings[CapacityCounters.DatastoreUsed]);
        Assert.Equal(50d * Gb, readings[CapacityCounters.DatastoreUncommitted]);
        Assert.Equal(120d * Gb, readings[CapacityCounters.DatastoreProvisioned]);

        Assert.All(snapshot.Observations, o =>
        {
            Assert.Equal(RollupType.Latest, o.Value.Rollup);
            Assert.Equal("bytes", o.Value.Unit);
            Assert.True(o.Value.IsAggregateInstance);
            Assert.Equal(T0, o.SampledAtUtc);
            Assert.Equal("vc-1", o.Source);
        });
    }

    [Fact]
    public async Task Capacity_series_names_are_not_vsphere_counter_names()
    {
        // Every vSphere counter ends in its rollup. These come from inventory
        // properties rather than the performance manager, and a name that
        // looked like disk.used.latest would send somebody to vCenter's
        // statistics level to explain a gap it had nothing to do with.
        var snapshot = await Read(Payload(datastores:
        [
            Store("vmfs01", capacity: 100 * Gb, free: 30 * Gb) with { UncommittedBytes = 0 },
        ]));

        Assert.Equal(5, snapshot.Observations.Count);
        Assert.All(snapshot.Observations, o =>
            Assert.EndsWith(".bytes", o.Value.CounterName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unread_figure_is_a_gap_not_a_zero()
    {
        // A zero written for an unread capacity reads as a volume that emptied,
        // and that is the first thing a forecast would fit a line to.
        var snapshot = await Read(Payload(datastores:
        [
            Store("nocapacity", capacity: null, free: 10 * Gb),
            Store("zerocapacity", capacity: 0, free: 0),
            Store("nofree", capacity: 100 * Gb, free: null),
            Store("nothin", capacity: 100 * Gb, free: 40 * Gb),
            Store("nonsense", capacity: 100 * Gb, free: 200 * Gb),
        ]));

        Assert.Empty(Readings(snapshot, "nocapacity"));
        Assert.Empty(Readings(snapshot, "zerocapacity"));

        Assert.Equal(new[] { CapacityCounters.DatastoreCapacity }, Readings(snapshot, "nofree").Keys);
        Assert.Equal(new[] { CapacityCounters.DatastoreCapacity }, Readings(snapshot, "nonsense").Keys);

        // Uncommitted is absent both when unreadable and on a volume with no
        // thin disks, and the two cannot be told apart.
        Assert.Equal(
            new[] { CapacityCounters.DatastoreCapacity, CapacityCounters.DatastoreFree, CapacityCounters.DatastoreUsed }
                .Order(),
            Readings(snapshot, "nothin").Keys.Order());
    }

    [Fact]
    public async Task An_inaccessible_datastore_reports_no_capacity()
    {
        // vCenter reports its size as whatever it last knew, or as zero.
        // Neither is a measurement.
        var snapshot = await Read(Payload(datastores:
        [
            Store("vmfs01", capacity: 100 * Gb, free: 0, accessible: false),
        ]));

        Assert.Empty(snapshot.Observations);
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

    // --- vCenter's own alarms ---------------------------------------------

    private static VsphereCluster Cluster() => new()
    {
        MoRef = "c-1",
        Name = "prod",
        HighAvailabilityEnabled = true,
        DrsEnabled = true,
    };

    private static VsphereTriggeredAlarm Alarm(
        string key = "115.1",
        string entity = "host-1",
        string entityType = "HostSystem",
        string? status = "red",
        string? name = "Host memory status",
        bool acknowledged = false) => new()
        {
            Key = key,
            EntityMoRef = entity,
            EntityType = entityType,
            AlarmMoRef = "alarm-115",
            AlarmName = name,
            AlarmDescription = "Default alarm to monitor memory.",
            OverallStatus = status,
            TriggeredAtUtc = T0.AddHours(-3),
            Acknowledged = acknowledged,
        };

    [Fact]
    public async Task A_red_host_now_has_an_alert_saying_why()
    {
        // The gap this closes. Against a live estate the product showed one
        // host Critical and, on the same screen, "no alert is currently firing
        // for this entity" — the colour came from summary.overallStatus and
        // the reason lived in a triggered alarm nobody read.
        var snapshot = await Read(Payload(hosts: [Host(status: "red")], alarms: [Alarm()]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Category == "vCenter");

        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("Host memory status", alert.Title);
        Assert.Equal(EntityId.For("vc-1", "host-1"), alert.Entity);
        Assert.Contains("clear it there", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_alarm_reported_on_both_a_host_and_its_cluster_raises_one_alert()
    {
        // Measured against a live vCenter: a memory alarm on host-3615 came
        // back on the host AND on its cluster, identical down to the key,
        // because vCenter propagates alarms up the tree. Keying on anything
        // but vCenter's own key counts one problem twice and reddens a cluster
        // that has nothing wrong with it.
        var snapshot = await Read(Payload(
            hosts: [Host()],
            clusters: [Cluster()],
            alarms: [Alarm(), Alarm()]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Category == "vCenter");

        Assert.Equal(EntityId.For("vc-1", "host-1"), alert.Entity);
    }

    [Fact]
    public async Task An_alarm_is_attributed_to_the_object_it_concerns_not_the_one_holding_it()
    {
        // The state found on a cluster names the host in its entity field.
        // Attributing to the holder would put a memory fault on a cluster.
        var snapshot = await Read(Payload(
            hosts: [Host()],
            clusters: [Cluster()],
            alarms: [Alarm(entity: "host-1")]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Category == "vCenter");

        Assert.NotEqual(EntityId.For("vc-1", "c-1"), alert.Entity);
        Assert.Equal(EntityId.For("vc-1", "host-1"), alert.Entity);
    }

    [Theory]
    [InlineData("green")]
    [InlineData("gray")]
    [InlineData(null)]
    public async Task A_status_that_is_not_a_problem_raises_nothing(string? status)
    {
        // Green is a triggered alarm that recovered and was never cleared;
        // gray is one vCenter cannot currently evaluate. Neither is news, and
        // gray in particular must not arrive as a crisis — nor as health.
        var snapshot = await Read(Payload(
            hosts: [Host()], alarms: [Alarm(status: status)]));

        Assert.DoesNotContain(snapshot.Alerts, a => a.Category == "vCenter");
    }

    [Fact]
    public async Task A_yellow_alarm_is_a_warning()
    {
        var snapshot = await Read(Payload(hosts: [Host()], alarms: [Alarm(status: "yellow")]));

        Assert.Equal(
            AlertSeverity.Warning,
            Assert.Single(snapshot.Alerts, a => a.Category == "vCenter").Severity);
    }

    [Fact]
    public async Task An_alarm_about_an_object_we_do_not_collect_is_counted_not_invented()
    {
        // vCenter raises alarms on datacentres, folders and resource pools,
        // none of which this collector reads. Attaching one to a nearby entity
        // would be a fabrication; dropping it silently would be the kind of
        // absence this product keeps being bitten by. So: counted, and said.
        var snapshot = await Read(Payload(
            hosts: [Host()],
            alarms: [Alarm(key: "9.1", entity: "datacenter-2", entityType: "Datacenter")]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Category == "vCenter");

        Assert.Equal("vCenter alarms on uncollected objects", alert.Title);
        Assert.Contains("1 alarm(s)", alert.Description, StringComparison.Ordinal);
        Assert.Equal(EntityId.For("vc-1", "vcenter"), alert.Entity);
    }

    [Fact]
    public async Task An_alarm_whose_name_could_not_be_read_is_still_reported()
    {
        // The name needs a second call, and a read-only account may be refused
        // it. An alarm under its reference is harder to read than one under
        // its name and infinitely better than one nobody is told about.
        var snapshot = await Read(Payload(hosts: [Host()], alarms: [Alarm(name: null)]));

        Assert.Equal(
            "vCenter alarm alarm-115",
            Assert.Single(snapshot.Alerts, a => a.Category == "vCenter").Title);
    }

    [Fact]
    public async Task A_vcenter_acknowledgement_is_reported_but_does_not_acknowledge_our_alert()
    {
        // The product keeps its own acknowledgement with its own audit trail.
        // Adopting vCenter's would show an alert as taken by somebody this
        // installation cannot name — and quietly silence it for our operator.
        var snapshot = await Read(Payload(
            hosts: [Host()],
            alarms: [Alarm(acknowledged: true) with { AcknowledgedByUser = "VSPHERE.LOCAL\\ops" }]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Category == "vCenter");

        Assert.Contains("Acknowledged in vCenter by", alert.Description, StringComparison.Ordinal);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public async Task The_fingerprint_survives_an_alarm_being_renamed()
    {
        // vCenter's key is alarmId.entityId. Fingerprinting on the name would
        // resolve the old alert and raise a new one the day somebody edits an
        // alarm's title, losing its history and waking everybody.
        var before = await Read(Payload(hosts: [Host()], alarms: [Alarm(name: "Host memory status")]));
        var after = await Read(Payload(hosts: [Host()], alarms: [Alarm(name: "Bellek durumu")]));

        Assert.Equal(
            Assert.Single(before.Alerts, a => a.Category == "vCenter").Fingerprint,
            Assert.Single(after.Alerts, a => a.Category == "vCenter").Fingerprint);
    }

    private static Entity Ds(InventorySnapshot snapshot, string name) =>
        snapshot.Entities.Single(e => e.Kind == EntityKind.Datastore && e.DisplayName == name);

    // --- storage identity -------------------------------------------------

    [Fact]
    public async Task A_datastore_carries_both_the_volume_and_the_devices_it_sits_on()
    {
        // Two vocabularies that do not overlap. vSphere names a datastore by
        // its VMFS UUID and names every storage path and disk device by an
        // NAA, so holding only one leaves "this datastore is slow" and "this
        // path has errors" as two facts about the same LUN that cannot be put
        // together.
        var snapshot = await Read(Payload(datastores:
        [
            new VsphereDatastore
            {
                MoRef = "ds-1",
                Name = "PRODVOL1",
                Accessible = true,
                Url = "ds:///vmfs/volumes/608bd301-3f719074-6962-f40343e85d10/",
                StorageDevices = ["naa.600508b1001cb736"],
            },
        ]));

        var marks = Ds(snapshot, "PRODVOL1").Marks;

        Assert.Contains(marks, m =>
            m.Kind == IdentityMarkKind.VolumeIdentifier &&
            m.Value == "608bd301-3f719074-6962-f40343e85d10");

        Assert.Contains(marks, m =>
            m.Kind == IdentityMarkKind.StorageDeviceId && m.Value == "naa.600508b1001cb736");
    }

    [Fact]
    public async Task A_spanned_datastore_reports_every_device()
    {
        // One extent per device, and a path error on any of them has to be
        // tie-able back here.
        var snapshot = await Read(Payload(datastores:
        [
            new VsphereDatastore
            {
                MoRef = "ds-1",
                Name = "SPANNED",
                Accessible = true,
                Url = "ds:///vmfs/volumes/aaaa/",
                StorageDevices = ["naa.111", "naa.222"],
            },
        ]));

        Assert.Equal(
            ["naa.111", "naa.222"],
            Ds(snapshot, "SPANNED").Marks
                .Where(m => m.Kind == IdentityMarkKind.StorageDeviceId)
                .Select(m => m.Value)
                .Order());
    }

    [Fact]
    public async Task A_datastore_with_no_device_reported_still_carries_its_volume()
    {
        // NFS has no storage device in this sense, and neither has a datastore
        // whose hosts could not be read. Either way the volume identity — the
        // one performance counters use — must survive, or the datastore stops
        // being measurable as well as unlocatable.
        var snapshot = await Read(Payload(datastores:
        [
            new VsphereDatastore
            {
                MoRef = "ds-1", Name = "NFSVOL", Accessible = true,
                Url = "ds:///vmfs/volumes/bbbb/",
            },
        ]));

        var marks = Ds(snapshot, "NFSVOL").Marks;

        Assert.Contains(marks, m => m.Kind == IdentityMarkKind.VolumeIdentifier);
        Assert.DoesNotContain(marks, m => m.Kind == IdentityMarkKind.StorageDeviceId);
    }

    [Theory]
    // A real one, trailing slash and all.
    [InlineData("ds:///vmfs/volumes/608bd301-3f719074-6962-f40343e85d10/", "608bd301-3f719074-6962-f40343e85d10")]
    // Without the trailing slash, which some versions omit.
    [InlineData("ds:///vmfs/volumes/608bd301-3f719074", "608bd301-3f719074")]
    // NFS: not a UUID at all, which is why the rule is "last segment" rather
    // than a UUID pattern. A pattern would have excluded every NFS datastore
    // in an estate, silently.
    [InlineData("ds:///vmfs/volumes/a1b2c3d4-e5f6/", "a1b2c3d4-e5f6")]
    public async Task A_datastore_url_yields_the_volume_identifier(string url, string expected)
    {
        var snapshot = await Read(Payload(datastores:
        [
            new VsphereDatastore { MoRef = "ds-1", Name = "VOL", Accessible = true, Url = url },
        ]));

        Assert.Equal(
            expected,
            Assert.Single(Ds(snapshot, "VOL").Marks, m => m.Kind == IdentityMarkKind.VolumeIdentifier)
                .Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Nothing but a scheme. "ds:" is shared by every datastore, so accepting it
    // would attach every volume's latency to whichever one was read last —
    // worse than having no identifier at all.
    [InlineData("ds://")]
    [InlineData("ds:")]
    public async Task A_url_that_names_no_volume_yields_no_mark(string? url)
    {
        var snapshot = await Read(Payload(datastores:
        [
            new VsphereDatastore { MoRef = "ds-1", Name = "VOL", Accessible = true, Url = url },
        ]));

        Assert.DoesNotContain(
            Ds(snapshot, "VOL").Marks,
            m => m.Kind == IdentityMarkKind.VolumeIdentifier);
    }

    // --- what the inventory now carries onward ----------------------------

    private static VsphereVirtualMachine SizedVm(
        string name = "app-db-01",
        int? vcpus = 8,
        long? memoryMb = 32768,
        long? cpuLimit = -1,
        long? memoryLimit = -1) => new()
    {
        MoRef = "vm-" + name,
        Name = name,
        PowerState = "poweredOn",
        OverallStatus = "green",
        VirtualCpuCount = vcpus,
        ConfiguredMemoryMb = memoryMb,
        CpuLimitMhz = cpuLimit,
        MemoryLimitMb = memoryLimit,
    };

    private static Entity Vm(InventorySnapshot snapshot, string name) =>
        snapshot.Entities.Single(e => e.Kind == EntityKind.VirtualMachine && e.DisplayName == name);

    [Fact]
    public async Task The_vCPU_count_reaches_the_entity_so_a_rule_can_divide_by_it()
    {
        // The point of collecting it at all. cpu.ready.summation is summed
        // across every vCPU, so a ready percentage is the raw total divided by
        // the interval and by this; CounterValue.AsPercentageOfInterval does
        // the first half and had nowhere to get the second. If this stops
        // arriving, the contention rule either states a figure wrong by a
        // factor of eight or states none at all.
        var snapshot = await Read(Payload(vms: [SizedVm()]));

        Assert.Equal(8, Vm(snapshot, "app-db-01").Sizing?.VirtualCpuCount);
        Assert.Equal(32768, Vm(snapshot, "app-db-01").Sizing?.ConfiguredMemoryMb);
    }

    [Fact]
    public async Task The_vCPU_count_is_not_an_identity_mark()
    {
        // Marks are weighed by the resolver to decide whether two records are
        // the same machine, and "8 vCPUs" is true of several thousand of them.
        // It is the same worthless evidence as the short hostname "10" derived
        // from 10.5.1.76, which this product has already been bitten by —
        // adding it as a mark would not be untidy, it would make identity
        // resolution actively worse.
        var snapshot = await Read(Payload(vms: [SizedVm()]));

        Assert.DoesNotContain(Vm(snapshot, "app-db-01").Marks, m => m.Value.Contains('8'));
    }

    [Fact]
    public async Task A_configured_limit_reaches_the_entity_and_unlimited_stays_distinguishable()
    {
        // A throttled machine waits exactly like a contended one while the
        // host is fine, so a contention rule needs to know which it is looking
        // at. Three states and all three matter: a real ceiling, the
        // platform's -1 for none, and null for a configuration nobody was
        // allowed to read.
        var limited = await Read(Payload(vms: [SizedVm("limited", cpuLimit: 4000)]));
        var free = await Read(Payload(vms: [SizedVm("free", cpuLimit: -1)]));
        var unread = await Read(Payload(vms: [SizedVm("unread", cpuLimit: null)]));

        Assert.True(Vm(limited, "limited").Sizing?.IsCpuLimited);
        Assert.False(Vm(free, "free").Sizing?.IsCpuLimited);
        Assert.Equal(-1, Vm(free, "free").Sizing?.CpuLimitMhz);
        Assert.Null(Vm(unread, "unread").Sizing?.CpuLimitMhz);
    }

    [Fact]
    public async Task A_machine_with_no_sizing_read_at_all_carries_none_rather_than_an_empty_one()
    {
        // One check instead of four, and no record full of nulls that a rule
        // could mistake for a machine with no processors.
        var snapshot = await Read(Payload(vms:
        [
            SizedVm("bare", vcpus: null, memoryMb: null, cpuLimit: null, memoryLimit: null),
        ]));

        Assert.Null(Vm(snapshot, "bare").Sizing);
    }

    [Fact]
    public async Task A_host_carries_its_storage_paths_so_a_dead_one_can_be_attributed()
    {
        // Counter map §5c's broken link. A storagePath fault counter names its
        // path vmhba0:C0:T0:L1, which carries no LUN identity, so a bus reset
        // could be attributed to a host and an HBA but never to the datastore
        // it took down. The NAA here is the same identifier the datastore
        // carries as a StorageDeviceId mark, and the two together are the
        // join. Lose this and the chain is broken again.
        var snapshot = await Read(Payload(hosts:
        [
            Host() with
            {
                StoragePaths =
                [
                    new VsphereStoragePath
                    {
                        Name = "vmhba0:C0:T0:L1",
                        State = "active",
                        Adapter = "vmhba0",
                        StorageDeviceId = "naa.600508b1001cb736",
                    },
                    new VsphereStoragePath
                    {
                        Name = "vmhba1:C0:T0:L1",
                        State = "dead",
                        Adapter = "vmhba1",
                        StorageDeviceId = "naa.600508b1001cb736",
                        DeviceKey = "key-vim.host.ScsiDisk-0200",
                    },
                ],
            },
        ]));

        var host = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost);

        Assert.Equal(2, host.StoragePaths.Count);
        Assert.Single(host.StoragePaths, p => p.IsDead);
        Assert.All(host.StoragePaths, p => Assert.Equal("naa.600508b1001cb736", p.StorageDeviceId));

        // The platform's key travels too, so paths can still be grouped per
        // device when the NAA lookup failed. Grouping on an empty name would
        // pile every unnamed device into one heap.
        Assert.Single(host.StoragePaths, p => p.DeviceKey == "key-vim.host.ScsiDisk-0200");
    }

    [Fact]
    public async Task A_standby_path_is_not_counted_as_lost()
    {
        // A standby path in an ALUA configuration is working and unused.
        // Counting it as dead would raise a redundancy alert about every
        // correctly configured array in the estate, which is how a real one
        // stops being read.
        var snapshot = await Read(Payload(hosts:
        [
            Host() with
            {
                StoragePaths = [new VsphereStoragePath { Name = "vmhba0:C0:T0:L1", State = "standby" }],
            },
        ]));

        Assert.All(
            snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost).StoragePaths,
            p => Assert.False(p.IsDead));
    }

    // --- host hardening inputs -------------------------------------------

    [Fact]
    public async Task A_host_carries_its_services_time_switch_policy_and_lockdown_mode()
    {
        // Carried for the compliance engine, not judged here: no alert is
        // raised about SSH or promiscuous mode by the inventory source.
        var snapshot = await Read(Payload(hosts:
        [
            Host() with
            {
                Services = [new HostService { Key = "TSM-SSH", Running = true, Policy = "off" }],
                TimeConfiguration = new TimeConfiguration { Protocol = "ntp", NtpServers = ["10.0.0.1"] },
                VirtualSwitchSecurity =
                [
                    new NetworkSecurityPolicy
                    {
                        Scope = NetworkPolicyScope.VirtualSwitch,
                        Name = "vSwitch0",
                        Configured = new SecurityPolicyFlags { ForgedTransmits = true },
                    },
                ],
                PortGroupSecurity = [],
                LockdownMode = "lockdownDisabled",
            },
        ]));

        var host = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost);

        Assert.Equal("TSM-SSH", Assert.Single(host.Services!).Key);
        Assert.Equal(["10.0.0.1"], host.TimeConfiguration!.NtpServers);
        Assert.True(Assert.Single(host.VirtualSwitchSecurity!).Configured.ForgedTransmits);
        Assert.NotNull(host.PortGroupSecurity);
        Assert.Empty(host.PortGroupSecurity);
        Assert.Equal("lockdownDisabled", host.LockdownMode);
        Assert.DoesNotContain(snapshot.Alerts, a => a.Category == "Configuration" &&
                                                    a.Entity == host.Id);
    }

    [Fact]
    public async Task A_host_whose_configuration_was_not_read_passes_null_through_rather_than_empty()
    {
        var snapshot = await Read(Payload(hosts: [Host()]));

        var host = snapshot.Entities.Single(e => e.Kind == EntityKind.EsxiHost);

        Assert.Null(host.Services);
        Assert.Null(host.TimeConfiguration);
        Assert.Null(host.VirtualSwitchSecurity);
        Assert.Null(host.PortGroupSecurity);
        Assert.Null(host.LockdownMode);
    }

    // --- thin overcommit --------------------------------------------------

    [Fact]
    public async Task A_datastore_that_has_promised_more_than_it_has_left_is_reported()
    {
        // Counter map §4: the only measure that warns long before a datastore
        // fills. This volume is 20% full, so the fullness alert says nothing
        // and will say nothing for months — and it is already certain to fill
        // if the thin disks merely grow into what they were given.
        var snapshot = await Read(Payload(datastores:
        [
            Store("vmfs01", capacity: 100 * Gb, free: 80 * Gb) with { UncommittedBytes = 300 * Gb },
        ]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Title == "Datastore over-committed");
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.DoesNotContain(snapshot.Alerts, a => a.Title == "Datastore nearly full");
    }

    [Fact]
    public async Task Promises_within_the_remaining_space_are_not_an_alert()
    {
        // Thin provisioning is a technique, not a fault. Alerting on its mere
        // presence would fire on every correctly run estate in existence, and
        // the comparison that means something is against what remains rather
        // than against capacity.
        var snapshot = await Read(Payload(datastores:
        [
            Store("vmfs01", capacity: 100 * Gb, free: 80 * Gb) with { UncommittedBytes = 40 * Gb },
        ]));

        Assert.DoesNotContain(snapshot.Alerts, a => a.Title == "Datastore over-committed");
    }

    [Fact]
    public async Task A_datastore_whose_uncommitted_space_was_not_read_is_not_called_over_committed()
    {
        // Absent on a datastore with no thin provisioning, and absent when the
        // account could not read it. Neither is a promise we may assert, and
        // the alert must not fire on the strength of a null.
        var snapshot = await Read(Payload(datastores:
        [
            Store("vmfs01", capacity: 100 * Gb, free: 80 * Gb),
        ]));

        Assert.DoesNotContain(snapshot.Alerts, a => a.Title == "Datastore over-committed");
    }

    // --- snapshots --------------------------------------------------------

    private static VsphereVirtualMachine WithSnapshot(
        DateTimeOffset? created,
        long? bytes = Gb,
        IReadOnlyList<string>? datastores = null,
        int count = 1) => new()
    {
        MoRef = "vm-1",
        Name = "fileserver",
        PowerState = "poweredOn",
        OverallStatus = "green",
        DatastoreMoRefs = datastores ?? [],
        SnapshotBytes = bytes,
        Snapshots =
        [
            .. Enumerable.Range(0, count).Select(i => new VsphereSnapshot
            {
                MoRef = "snapshot-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = "before upgrade",
                CreatedAtUtc = created,
                Depth = i + 1,
            }),
        ],
    };

    [Fact]
    public async Task A_snapshot_nobody_has_deleted_is_reported()
    {
        // vROps has no snapshot alert at all — it reads the tree only as a
        // guard, "does this machine have one". So this is differentiation
        // rather than parity, and it is the most common self-inflicted outage
        // in a VMware estate: nobody forgets on purpose, somebody takes one
        // before a change and the change goes fine.
        var snapshot = await Read(Payload(vms: [WithSnapshot(T0.AddDays(-20))]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Title == "Snapshot left behind");
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("20 days old", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_snapshot_taken_this_morning_is_not_an_alert()
    {
        // A snapshot taken before a change is doing its job. Alerting the
        // moment one exists would make the alert meaningless by the second day
        // of use, which is what vROps's guard-only reading implicitly admits.
        var snapshot = await Read(Payload(vms: [WithSnapshot(T0.AddHours(-4))]));

        Assert.DoesNotContain(snapshot.Alerts, a => a.Title == "Snapshot left behind");
    }

    [Fact]
    public async Task A_snapshot_larger_than_the_free_space_beneath_it_is_critical_whatever_its_age()
    {
        // Not a threshold on size, because no size is wrong in itself. At this
        // point the outage is arithmetic rather than a risk — and worse, the
        // obvious remedy is not available: consolidating a snapshot needs room
        // on the same volume, so somebody has to plan rather than click.
        var snapshot = await Read(Payload(
            vms: [WithSnapshot(T0.AddHours(-2), bytes: 90 * Gb, datastores: ["ds-vmfs01"])],
            datastores: [Store("vmfs01", capacity: 100 * Gb, free: 10 * Gb)]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Title == "Snapshot left behind");
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("deleting it needs planning", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_growing_chain_stays_one_alert_rather_than_one_per_snapshot()
    {
        // A chain that gains a link is the same problem getting worse. An
        // inbox that gained a row every time somebody took another snapshot
        // would be teaching people to ignore the whole category. Depth is said
        // as well as count because they are different problems: four snapshots
        // side by side is somebody being careful, four deep is four delta
        // disks every read has to walk.
        var snapshot = await Read(Payload(vms: [WithSnapshot(T0.AddDays(-30), count: 4)]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Title == "Snapshot left behind");
        Assert.Contains("4 snapshots 4 deep", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_snapshot_whose_size_could_not_be_read_says_so_rather_than_saying_zero()
    {
        // Zero looks like a measurement, and the counter map calls that this
        // product's most dangerous number: an operator reading "0 GB" strikes
        // snapshots off the list when in fact nobody looked.
        var snapshot = await Read(Payload(vms: [WithSnapshot(T0.AddDays(-20), bytes: null)]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Title == "Snapshot left behind");
        Assert.Contains("an unmeasured amount", alert.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("0 GB", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_with_no_snapshots_raises_nothing()
    {
        var snapshot = await Read(Payload(vms: [SizedVm()]));

        Assert.DoesNotContain(snapshot.Alerts, a => a.Title == "Snapshot left behind");
        Assert.Empty(snapshot.SnapshotFindings);
    }

    [Fact]
    public async Task A_stale_snapshot_carries_what_is_needed_to_name_its_creator()
    {
        // The collector does not look the creator up — that is stored history,
        // and a collector reads its source, not the product's store. It hands
        // over the machine and each snapshot's time, tied to the alert.
        var snapshot = await Read(Payload(vms: [WithSnapshot(T0.AddDays(-20), count: 2)]));

        var alert = Assert.Single(snapshot.Alerts, a => a.Title == "Snapshot left behind");
        var finding = Assert.Single(snapshot.SnapshotFindings);
        Assert.Equal(alert.Fingerprint, finding.Fingerprint);
        Assert.Equal("vm-1", finding.VmMoRef);
        Assert.Equal(2, finding.Snapshots.Count);
        Assert.All(finding.Snapshots, s => Assert.Equal(T0.AddDays(-20), s.CreatedAtUtc));
    }

    [Fact]
    public async Task A_snapshot_too_young_to_report_carries_no_finding()
    {
        var snapshot = await Read(Payload(vms: [WithSnapshot(T0.AddHours(-4))]));

        Assert.Empty(snapshot.SnapshotFindings);
    }
}
