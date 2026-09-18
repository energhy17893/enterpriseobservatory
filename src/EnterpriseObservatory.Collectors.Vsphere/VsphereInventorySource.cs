using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Turns one vCenter's inventory into entities and relationships.
/// </summary>
/// <remarks>
/// <para>
/// This collector is the single source of truth for the ESXi host list. BMC and
/// management-appliance collectors contribute hardware telemetry to hosts that
/// already exist here; they never create one. That rule is what makes counting
/// the same physical box twice structurally impossible — see product principle
/// 2 and ADR-0005.
/// </para>
/// <para>
/// It reports identity <em>marks</em> and never decides identity. Whether an
/// iLO record and a host are the same machine is the resolver's call, made from
/// all collectors' marks at once, because no single collector has enough
/// information to make it — see ADR-0003.
/// </para>
/// </remarks>
public sealed class VsphereInventorySource(IVsphereInventoryApi api, IClock clock) : IInventorySource
{
    private readonly IVsphereInventoryApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public string InstanceId => _api.InstanceId;

    public async Task<InventorySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var payload = await _api.RetrieveInventoryAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var entities = new List<Entity>();
        var relationships = new List<Relationship>();
        var alerts = new List<AlertDefinition>();

        // Managed-object references are unique within a vCenter but not between
        // them, so they are qualified before becoming entity ids.
        EntityId Id(string moRef) => new($"{InstanceId}/{moRef}");

        var vCenter = new Entity
        {
            Id = Id("vcenter"),
            Kind = EntityKind.VCenter,
            DisplayName = payload.VCenterName,
            Health = HealthState.Healthy,
            LastSeenUtc = now,
            Marks = [IdentityMark.Create(IdentityMarkKind.Fqdn, payload.VCenterName, InstanceId)],
        };
        entities.Add(vCenter);

        AddClusters(payload, Id, now, entities, relationships, alerts);
        AddHosts(payload, Id, now, vCenter.Id, entities, relationships, alerts);
        AddDatastores(payload, Id, now, entities, relationships);
        AddVirtualMachines(payload, Id, now, entities, relationships);

        return new InventorySnapshot
        {
            SourceInstanceId = InstanceId,
            ReadAtUtc = now,
            Entities = entities,
            Relationships = relationships,
            Alerts = alerts,
            Failures = [.. payload.Failures.Select(ToFailure)],
        };
    }

    private void AddClusters(
        VsphereInventoryPayload payload,
        Func<string, EntityId> id,
        DateTimeOffset now,
        List<Entity> entities,
        List<Relationship> relationships,
        List<AlertDefinition> alerts)
    {
        foreach (var cluster in payload.Clusters)
        {
            entities.Add(new Entity
            {
                Id = id(cluster.MoRef),
                Kind = EntityKind.Cluster,
                DisplayName = cluster.Name,
                // A cluster's own health is derived from its members by the
                // health-propagation pass, not asserted here.
                Health = HealthState.Unknown,
                LastSeenUtc = now,
            });

            if (cluster.HighAvailabilityEnabled is null || cluster.DrsEnabled is null)
            {
                // The previous product got this right and it is worth keeping:
                // when the configuration cannot be read, HA and DRS are not
                // written as enabled. Saying "HA is off" when we simply could
                // not look would send someone to fix a cluster that is fine —
                // or leave one that is not.
                alerts.Add(new AlertDefinition
                {
                    Fingerprint = AlertFingerprint.Create(
                        InstanceId, "Cluster configuration unreadable", "Configuration",
                        cluster.Name, "cluster-config-unreadable"),
                    Severity = AlertSeverity.Warning,
                    Title = "Cluster configuration unreadable",
                    Description =
                        $"HA and DRS settings for '{cluster.Name}' could not be read, so they are " +
                        "reported as Unknown rather than assumed. Best-practice checks that depend " +
                        "on them are skipped for this cluster.",
                    Category = "Configuration",
                    Source = InstanceId,
                    Entity = id(cluster.MoRef),
                });
            }

            relationships.Add(Edge(id(cluster.MoRef), id("vcenter"), RelationshipKind.ManagedBy, now));
        }
    }

    private void AddHosts(
        VsphereInventoryPayload payload,
        Func<string, EntityId> id,
        DateTimeOffset now,
        EntityId vCenterId,
        List<Entity> entities,
        List<Relationship> relationships,
        List<AlertDefinition> alerts)
    {
        foreach (var host in payload.Hosts)
        {
            var reachable = IsReachable(host.ConnectionState);

            entities.Add(new Entity
            {
                Id = id(host.MoRef),
                Kind = EntityKind.EsxiHost,
                DisplayName = host.Name,
                Marks = MarksFor(host),
                // A host vCenter cannot reach has unknown health regardless of
                // the colour it was last painted.
                Health = reachable ? MapStatus(host.OverallStatus) : HealthState.Unknown,
                ObservationState = host.InMaintenanceMode
                    ? ObservationState.InMaintenance
                    : ObservationState.Active,
                LastSeenUtc = now,
            });

            relationships.Add(Edge(id(host.MoRef), vCenterId, RelationshipKind.ManagedBy, now));

            if (!string.IsNullOrWhiteSpace(host.ClusterMoRef))
            {
                relationships.Add(Edge(id(host.MoRef), id(host.ClusterMoRef), RelationshipKind.PartOf, now));
            }

            if (!reachable)
            {
                alerts.Add(new AlertDefinition
                {
                    Fingerprint = AlertFingerprint.Create(
                        InstanceId, "Host not reachable from vCenter", "Inventory",
                        host.Name, "host-disconnected"),
                    Severity = AlertSeverity.Critical,
                    Title = "Host not reachable from vCenter",
                    Description =
                        $"Connection state is '{host.ConnectionState}'. Everything on this host is " +
                        "reported as Unknown; it is not being claimed healthy and it is not being " +
                        "claimed down.",
                    Category = "Inventory",
                    Source = InstanceId,
                    Entity = id(host.MoRef),
                });
            }
        }
    }

    private static void AddDatastores(
        VsphereInventoryPayload payload,
        Func<string, EntityId> id,
        DateTimeOffset now,
        List<Entity> entities,
        List<Relationship> relationships)
    {
        foreach (var datastore in payload.Datastores)
        {
            entities.Add(new Entity
            {
                Id = id(datastore.MoRef),
                Kind = EntityKind.Datastore,
                DisplayName = datastore.Name,
                // Accessible is nullable on purpose: unreadable is not the same
                // as inaccessible, and neither is the same as fine.
                Health = datastore.Accessible switch
                {
                    true => HealthState.Healthy,
                    false => HealthState.Critical,
                    null => HealthState.Unknown,
                },
                LastSeenUtc = now,
            });

            relationships.Add(Edge(id(datastore.MoRef), id("vcenter"), RelationshipKind.ManagedBy, now));
        }
    }

    private static void AddVirtualMachines(
        VsphereInventoryPayload payload,
        Func<string, EntityId> id,
        DateTimeOffset now,
        List<Entity> entities,
        List<Relationship> relationships)
    {
        var knownHosts = payload.Hosts.Select(h => h.MoRef).ToHashSet(StringComparer.Ordinal);
        var knownDatastores = payload.Datastores.Select(d => d.MoRef).ToHashSet(StringComparer.Ordinal);

        foreach (var vm in payload.VirtualMachines)
        {
            var poweredOn = string.Equals(vm.PowerState, "poweredOn", StringComparison.OrdinalIgnoreCase);

            entities.Add(new Entity
            {
                Id = id(vm.MoRef),
                Kind = EntityKind.VirtualMachine,
                DisplayName = vm.Name,
                Marks = string.IsNullOrWhiteSpace(vm.InstanceUuid)
                    ? []
                    : [IdentityMark.Create(IdentityMarkKind.HardwareUuid, vm.InstanceUuid, InstanceIdOf(payload))],
                // A powered-off VM is not unhealthy, but neither is it observed.
                // Reporting it healthy would be claiming something we did not
                // measure.
                Health = poweredOn ? MapStatus(vm.OverallStatus) : HealthState.Unknown,
                LastSeenUtc = now,
            });

            // Only edges to things we actually saw. A reference to a host in
            // another vCenter, or one we failed to read, would otherwise become
            // an edge to an entity that does not exist.
            if (!string.IsNullOrWhiteSpace(vm.HostMoRef) && knownHosts.Contains(vm.HostMoRef))
            {
                relationships.Add(Edge(id(vm.MoRef), id(vm.HostMoRef), RelationshipKind.RunsOn, now));
            }

            foreach (var datastore in vm.DatastoreMoRefs.Where(knownDatastores.Contains))
            {
                relationships.Add(Edge(id(vm.MoRef), id(datastore), RelationshipKind.BackedBy, now));
            }
        }
    }

    private List<IdentityMark> MarksFor(VsphereHost host)
    {
        var marks = new List<IdentityMark>();

        if (!string.IsNullOrWhiteSpace(host.HardwareUuid))
        {
            marks.Add(IdentityMark.Create(IdentityMarkKind.HardwareUuid, host.HardwareUuid, InstanceId));
        }

        foreach (var ip in host.IpAddresses.Where(ip => !string.IsNullOrWhiteSpace(ip)))
        {
            marks.Add(IdentityMark.Create(IdentityMarkKind.IpAddress, ip, InstanceId));
        }

        // vCenter names a host by whatever it was added as: sometimes an FQDN,
        // sometimes a bare address. Both spellings are reported and the
        // resolver decides what they are worth.
        var name = string.IsNullOrWhiteSpace(host.Fqdn) ? host.Name : host.Fqdn;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (name.Contains('.', StringComparison.Ordinal))
            {
                marks.Add(IdentityMark.Create(IdentityMarkKind.Fqdn, name, InstanceId));
                marks.Add(IdentityMark.Create(
                    IdentityMarkKind.ShortHostname, name[..name.IndexOf('.', StringComparison.Ordinal)], InstanceId));
            }
            else
            {
                marks.Add(IdentityMark.Create(IdentityMarkKind.ShortHostname, name, InstanceId));
            }
        }

        return marks;
    }

    private static string InstanceIdOf(VsphereInventoryPayload payload) => payload.VCenterName;

    private static Relationship Edge(EntityId from, EntityId to, RelationshipKind kind, DateTimeOffset now) =>
        new() { From = from, To = to, Kind = kind, ObservedAtUtc = now };

    private static bool IsReachable(string? connectionState) =>
        string.Equals(connectionState, "connected", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps vSphere's health colour.
    /// </summary>
    /// <remarks>
    /// Gray means vSphere itself does not know, and an unrecognised value means
    /// we do not — both are Unknown. Defaulting an unfamiliar colour to healthy
    /// is how a monitoring product ends up green during an outage.
    /// </remarks>
    private static HealthState MapStatus(string? overallStatus) =>
        overallStatus?.Trim().ToLowerInvariant() switch
        {
            "green" => HealthState.Healthy,
            "yellow" => HealthState.Warning,
            "red" => HealthState.Critical,
            _ => HealthState.Unknown,
        };

    private static CollectionFailure ToFailure(VsphereReadFailure failure) => new()
    {
        Kind = failure.IsPermissionDenied
            ? CollectionFailureKind.AuthorizationDenied
            : CollectionFailureKind.ProtocolError,
        Target = failure.Target,
        Detail = failure.Detail,
    };
}
