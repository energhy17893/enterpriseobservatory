namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// The inventory calls this collector makes.
/// </summary>
/// <remarks>
/// Separate from <see cref="IVsphereApi"/> because inventory and metrics are
/// different rhythms (ADR-0005). A concrete client may implement both over one
/// session; the sources stay independently testable either way.
/// </remarks>
public interface IVsphereInventoryApi
{
    string InstanceId { get; }

    Task<VsphereInventoryPayload> RetrieveInventoryAsync(CancellationToken cancellationToken);
}

/// <summary>Inventory as one vCenter reports it, before any interpretation.</summary>
/// <remarks>
/// Deliberately close to the wire. Turning it into entities and relationships
/// is the source's job, and keeping the two apart is what lets the mapping be
/// tested without a server.
/// </remarks>
public sealed record VsphereInventoryPayload
{
    /// <summary>The vCenter's own name, for the management-plane entity.</summary>
    public required string VCenterName { get; init; }

    public IReadOnlyList<VsphereHost> Hosts { get; init; } = [];

    public IReadOnlyList<VsphereVirtualMachine> VirtualMachines { get; init; } = [];

    public IReadOnlyList<VsphereCluster> Clusters { get; init; } = [];

    public IReadOnlyList<VsphereDatastore> Datastores { get; init; } = [];

    /// <summary>Alarms vCenter itself currently has raised.</summary>
    public IReadOnlyList<VsphereTriggeredAlarm> TriggeredAlarms { get; init; } = [];

    /// <summary>Parts of the inventory that could not be read.</summary>
    public IReadOnlyList<VsphereReadFailure> Failures { get; init; } = [];

    /// <summary>
    /// How many pages were fetched to build this.
    /// </summary>
    /// <remarks>
    /// Reported so that following the continuation token is observable rather
    /// than assumed. An environment that fits in one page never proves the
    /// paging logic works, and stopping early looks like a small healthy estate
    /// rather than a bug.
    /// </remarks>
    public int PagesRetrieved { get; init; } = 1;
}

/// <summary>Something the client could not read, in its own terms.</summary>
public sealed record VsphereReadFailure
{
    public required string Target { get; init; }

    public required string Detail { get; init; }

    /// <summary>True when the server refused for lack of privilege.</summary>
    public bool IsPermissionDenied { get; init; }
}

/// <summary>
/// An alarm vCenter has raised, and the object it is about.
/// </summary>
/// <remarks>
/// <para>
/// vCenter propagates a triggered alarm up the inventory tree: a memory alarm
/// on a host is reported on the host <em>and</em> on its cluster, with
/// identical contents. The object it was found on is therefore not the object
/// it concerns — <see cref="EntityMoRef"/> is, and it is what this carries.
/// Attributing to the holder instead would light up a cluster that has nothing
/// wrong with it and count one problem twice.
/// </para>
/// <para>
/// <see cref="Key"/> is vCenter's own, in the form <c>alarmId.entityId</c>. It
/// is stable across cycles and across the objects the alarm appears on, which
/// makes it both the way to de-duplicate and the natural fingerprint.
/// </para>
/// </remarks>
public sealed record VsphereTriggeredAlarm
{
    /// <summary>vCenter's identity for this triggering, e.g. <c>115.3615</c>.</summary>
    public required string Key { get; init; }

    /// <summary>The object the alarm is about, e.g. <c>host-3615</c>.</summary>
    public required string EntityMoRef { get; init; }

    /// <summary>Its type, e.g. <c>HostSystem</c>. Empty when vCenter omitted it.</summary>
    public string EntityType { get; init; } = string.Empty;

    /// <summary>The alarm definition's reference, e.g. <c>alarm-115</c>.</summary>
    public required string AlarmMoRef { get; init; }

    /// <summary>
    /// The alarm's name, when it could be resolved.
    /// </summary>
    /// <remarks>
    /// The state carries only a reference; the name lives on the definition and
    /// costs a second call. Null means that call did not answer — the alarm is
    /// still real and still reported, under its reference.
    /// </remarks>
    public string? AlarmName { get; init; }

    public string? AlarmDescription { get; init; }

    /// <summary>red, yellow, green or gray.</summary>
    public string? OverallStatus { get; init; }

    /// <summary>When vCenter raised it.</summary>
    public DateTimeOffset? TriggeredAtUtc { get; init; }

    /// <summary>
    /// Whether somebody acknowledged it in vCenter.
    /// </summary>
    /// <remarks>
    /// Carried, not acted on. The product has its own acknowledgement with its
    /// own audit trail, and silently adopting vCenter's would mean an alert
    /// showing as acknowledged by nobody this installation can name.
    /// </remarks>
    public bool Acknowledged { get; init; }

    public string? AcknowledgedByUser { get; init; }
}

/// <summary>An ESXi host as vCenter sees it.</summary>
/// <remarks>
/// vCenter is the single source of truth for the host list. No BMC or
/// management-appliance collector may create one — see product principle 2 and
/// ADR-0005.
/// </remarks>
public sealed record VsphereHost
{
    /// <summary>Managed object reference, e.g. <c>host-123</c>.</summary>
    public required string MoRef { get; init; }

    public required string Name { get; init; }

    /// <summary>SMBIOS/hardware UUID. The strongest identity evidence available.</summary>
    public string? HardwareUuid { get; init; }

    public IReadOnlyList<string> IpAddresses { get; init; } = [];

    public string? Fqdn { get; init; }

    /// <summary>vSphere's own health colour: green, yellow, red or gray.</summary>
    public string? OverallStatus { get; init; }

    /// <summary>connected, disconnected or notResponding.</summary>
    public string? ConnectionState { get; init; }

    public bool InMaintenanceMode { get; init; }

    public string? ClusterMoRef { get; init; }
}

/// <summary>A virtual machine as vCenter sees it.</summary>
public sealed record VsphereVirtualMachine
{
    public required string MoRef { get; init; }

    public required string Name { get; init; }

    public string? InstanceUuid { get; init; }

    /// <summary>poweredOn, poweredOff or suspended.</summary>
    public string? PowerState { get; init; }

    public string? OverallStatus { get; init; }

    /// <summary>The host it is running on, when it is running.</summary>
    public string? HostMoRef { get; init; }

    public IReadOnlyList<string> DatastoreMoRefs { get; init; } = [];
}

/// <summary>A cluster as vCenter sees it.</summary>
public sealed record VsphereCluster
{
    public required string MoRef { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// Whether HA is enabled, or null when the configuration could not be read.
    /// </summary>
    /// <remarks>
    /// Null rather than false. "We could not read the HA setting" and "HA is
    /// off" lead to opposite actions, and a best-practice check that treats
    /// the first as the second will raise an alarm about a cluster that is
    /// perfectly well configured — or, worse, stay quiet about one that is not.
    /// </remarks>
    public bool? HighAvailabilityEnabled { get; init; }

    /// <summary>Whether DRS is enabled, or null when unreadable. See <see cref="HighAvailabilityEnabled"/>.</summary>
    public bool? DrsEnabled { get; init; }
}

/// <summary>A datastore as vCenter sees it.</summary>
public sealed record VsphereDatastore
{
    public required string MoRef { get; init; }

    public required string Name { get; init; }

    public long? CapacityBytes { get; init; }

    public long? FreeSpaceBytes { get; init; }

    /// <summary>Null when unreadable — not assumed reachable.</summary>
    public bool? Accessible { get; init; }

    /// <summary>VMFS, NFS, vsan and so on.</summary>
    public string? Type { get; init; }

    /// <summary>
    /// Where the datastore lives, e.g. <c>ds:///vmfs/volumes/5f2c.../</c>.
    /// </summary>
    /// <remarks>
    /// Requested for what is inside it rather than for display. vCenter does
    /// not collect per-datastore latency on the datastore; it collects it on
    /// each host, with the datastore's volume identifier as the counter
    /// instance. This URL is the only thing in the inventory that carries that
    /// identifier, so it is what joins a number measured on a host to the
    /// datastore the number is about.
    /// </remarks>
    public string? Url { get; init; }

    /// <summary>
    /// The storage devices this datastore's volume occupies, as NAA names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty for anything that is not block storage, and for a datastore whose
    /// hosts could not be read. More than one for a spanned VMFS volume, which
    /// is why this is a list: keeping only the first would report a datastore
    /// as living on one LUN and quietly lose the others, and for a spanned
    /// volume the lost half is the half that explains the outage.
    /// </para>
    /// <para>
    /// This is the link that was missing. A datastore is named by a VMFS UUID
    /// and every storage path and disk device by an NAA, so without it
    /// "this datastore is slow" and "this path has errors" are two facts about
    /// the same LUN that cannot be put together.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> StorageDevices { get; init; } = [];
}
