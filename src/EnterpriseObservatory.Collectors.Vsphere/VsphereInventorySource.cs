using System.Globalization;
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
        EntityId Id(string moRef) => EntityId.For(InstanceId, moRef);

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
        AddDatastores(payload, Id, now, entities, relationships, alerts);
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

    private void AddDatastores(
        VsphereInventoryPayload payload,
        Func<string, EntityId> id,
        DateTimeOffset now,
        List<Entity> entities,
        List<Relationship> relationships,
        List<AlertDefinition> alerts)
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

            // Health said Critical and nothing reached the inbox. The same gap
            // the collector-unreachable alert exists to close, left open for
            // the one entity kind where it means an outage rather than a
            // degradation: virtual machines on an inaccessible datastore are
            // not slow, they are stopped.
            if (datastore.Accessible == false)
            {
                alerts.Add(new AlertDefinition
                {
                    Fingerprint = AlertFingerprint.Create(
                        InstanceId, "Datastore not accessible", "Inventory",
                        datastore.Name, "datastore-inaccessible"),
                    Severity = AlertSeverity.Critical,
                    Title = "Datastore not accessible",
                    Description =
                        $"vCenter reports '{datastore.Name}' as inaccessible. Virtual machines " +
                        "stored on it cannot read or write, and anything this datastore is the " +
                        "only copy of is unavailable.",
                    Category = "Inventory",
                    Source = InstanceId,
                    Entity = id(datastore.MoRef),
                });
            }

            if (Fullness(datastore) is { } fullness)
            {
                AddFullnessAlert(datastore, fullness, id(datastore.MoRef), InstanceId, alerts);
            }
        }
    }

    /// <summary>
    /// How full a datastore is, or null when the numbers cannot say.
    /// </summary>
    /// <remarks>
    /// Zero capacity is refused rather than divided by: it would read as
    /// completely full, which is the most alarming possible answer to arrive at
    /// by accident. Absent values mean the properties were not readable, and an
    /// unreadable datastore is not a full one.
    /// </remarks>
    private static double? Fullness(VsphereDatastore datastore) =>
        datastore is { CapacityBytes: > 0 and { } capacity, FreeSpaceBytes: >= 0 and { } free }
            ? (capacity - free) / (double)capacity * 100d
            : null;

    /// <summary>
    /// Raises the fullness alert, when there is one to raise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The data for this was already being fetched. <c>summary.capacity</c> and
    /// <c>summary.freeSpace</c> have been requested from vCenter on every
    /// inventory cycle since the collector was written, parsed, and then
    /// dropped on the floor — no entity field, no series, no alert, nothing on
    /// screen. "This datastore is nearly full" is the most commonly configured
    /// alert in VMware monitoring and the product could not say it, for want of
    /// using what it already had.
    /// </para>
    /// <para>
    /// The free space is in the message beside the percentage, deliberately.
    /// Ninety percent of a hundred-terabyte volume is ten terabytes free and
    /// nobody's emergency; ninety percent of a five-hundred-gigabyte one is
    /// fifty gigabytes and somebody's weekend. A percentage alone cannot tell
    /// those apart and an operator should not have to go and look.
    /// </para>
    /// </remarks>
    private static void AddFullnessAlert(
        VsphereDatastore datastore,
        double fullness,
        EntityId entity,
        string instanceId,
        List<AlertDefinition> alerts)
    {
        var severity = fullness switch
        {
            >= CriticalFullnessPercent => AlertSeverity.Critical,
            >= WarningFullnessPercent => AlertSeverity.Warning,
            _ => (AlertSeverity?)null,
        };

        if (severity is not { } level)
        {
            return;
        }

        var free = datastore.FreeSpaceBytes ?? 0;

        alerts.Add(new AlertDefinition
        {
            // One fingerprint across both severities, so a datastore crossing
            // from warning to critical raises the same alert rather than a
            // second one beside it — the inbox should say the problem got
            // worse, not that a new problem appeared.
            Fingerprint = AlertFingerprint.Create(
                instanceId, "Datastore nearly full", "Capacity",
                datastore.Name, "datastore-full"),
            Severity = level,
            Title = "Datastore nearly full",
            Description = string.Create(
                CultureInfo.InvariantCulture,
                $"'{datastore.Name}' is {fullness:0.#}% full, with {Gigabytes(free):0.#} GB free " +
                $"of {Gigabytes(datastore.CapacityBytes ?? 0):0.#} GB."),
            Category = "Capacity",
            Source = instanceId,
            Entity = entity,
        });
    }

    /// <summary>Warn here. Not a crisis, but the point where somebody plans.</summary>
    private const double WarningFullnessPercent = 85d;

    /// <summary>
    /// And here it is a crisis.
    /// </summary>
    /// <remarks>
    /// A VMFS datastore that fills completely does not degrade: every virtual
    /// machine with a snapshot or a thin disk on it stops, at once.
    /// </remarks>
    private const double CriticalFullnessPercent = 95d;

    private static double Gigabytes(long bytes) => bytes / 1024d / 1024d / 1024d;

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

        // vCenter names a host by whatever it was added as: an FQDN, a short
        // name, or a bare IP address. Which of those it is has to be decided
        // here, because the resolver weighs the kinds differently.
        var name = string.IsNullOrWhiteSpace(host.Fqdn) ? host.Name : host.Fqdn;
        marks.AddRange(MarksForName(name));

        return marks;
    }

    /// <summary>
    /// Classifies whatever vCenter calls a host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An address is checked for first. It contains dots, so a naive FQDN test
    /// accepts it — and then derives a "short hostname" from the text before
    /// the first dot, which for <c>10.5.1.76</c> is <c>10</c>. Every host in
    /// the network would carry that same mark, which is exactly the kind of
    /// worthless evidence that turns into a wrong match.
    /// </para>
    /// <para>
    /// Found by running against a live vCenter whose hosts are registered by
    /// address.
    /// </para>
    /// </remarks>
    private IEnumerable<IdentityMark> MarksForName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield break;
        }

        var trimmed = name.Trim();

        if (System.Net.IPAddress.TryParse(trimmed, out _))
        {
            yield return IdentityMark.Create(IdentityMarkKind.IpAddress, trimmed, InstanceId);
            yield break;
        }

        var firstDot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (firstDot > 0)
        {
            yield return IdentityMark.Create(IdentityMarkKind.Fqdn, trimmed, InstanceId);
            yield return IdentityMark.Create(
                IdentityMarkKind.ShortHostname, trimmed[..firstDot], InstanceId);
        }
        else
        {
            yield return IdentityMark.Create(IdentityMarkKind.ShortHostname, trimmed, InstanceId);
        }
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
