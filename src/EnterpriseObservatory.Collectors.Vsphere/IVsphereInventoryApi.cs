using EnterpriseObservatory.Application.Collection;
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

    /// <summary>How much of what was asked for actually came back.</summary>
    public IReadOnlyList<PropertyCoverage> Coverage { get; init; } = [];

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

    /// <summary>
    /// Every route this host has to a storage device, and the state of each.
    /// </summary>
    /// <remarks>
    /// Empty when the path table could not be read, which is not the same as a
    /// host with no storage — an unreadable table must not be reported as a
    /// host that has lost every path.
    /// </remarks>
    public IReadOnlyList<VsphereStoragePath> StoragePaths { get; init; } = [];
    /// <summary>
    /// The host advanced settings this product carries, by their vSphere name.
    /// </summary>
    /// <remarks>
    /// A filtered view rather than the whole table — see
    /// <see cref="AdvancedSettings"/> for which names and why. Empty when the
    /// host did not report <c>config.option</c> at all, and **a missing key is
    /// not an empty value**: a syslog target nobody set arrives as an empty
    /// string, while a host that refused the property arrives with no entry.
    /// A rule that conflates them reports a finding against a host it could
    /// not read.
    /// </remarks>
    public IReadOnlyDictionary<string, string> AdvancedSettings { get; init; } =
        Vsphere.AdvancedSettings.None;

    // The configuration below is carried in the domain's own records rather
    // than in wire-shaped twins. They hold the platform's words untranslated,
    // exactly as a twin would, so a second copy would be a mapping with
    // nothing to map. Null throughout means "not reported", never "none".

    /// <summary>From <c>config.service</c>; null when not reported.</summary>
    public IReadOnlyList<Domain.HostService>? Services { get; init; }

    /// <summary>From <c>config.dateTimeInfo</c>; null when not reported.</summary>
    public Domain.TimeConfiguration? TimeConfiguration { get; init; }

    /// <summary>From <c>config.network.vswitch</c>; null when not reported.</summary>
    public IReadOnlyList<Domain.NetworkSecurityPolicy>? VirtualSwitchSecurity { get; init; }

    /// <summary>From <c>config.network.portgroup</c>; null when not reported.</summary>
    public IReadOnlyList<Domain.NetworkSecurityPolicy>? PortGroupSecurity { get; init; }

    /// <summary>From <c>config.lockdownMode</c>; null when not reported.</summary>
    public string? LockdownMode { get; init; }
}

/// <summary>One entry of a host's multipath table, as vCenter reports it.</summary>
/// <remarks>
/// <para>
/// Read from <c>config.storageDevice.multipathInfo</c>, which is an array of
/// <c>HostMultipathInfoLogicalUnit</c>: one per device, each carrying the
/// paths that reach it. The device is named there by an internal key rather
/// than by the NAA the rest of the product speaks, so
/// <c>config.storageDevice.scsiLun</c> is read alongside it purely to turn
/// that key into a canonical name.
/// </para>
/// <para>
/// <strong>Unverified against a live vCenter.</strong> The shape follows the
/// published vim25 schema. The one structure in this collector that <em>was</em>
/// dumped from a real server turned out not to match what its name implied —
/// <c>volume</c> is a sibling of <c>mountInfo</c>, not a child — so this
/// should be treated as a reasonable reading rather than a measured one until
/// somebody points it at a server. The parsing is covered by tests against XML
/// in the schema's shape; that proves the reader, not the schema.
/// </para>
/// </remarks>
public sealed record VsphereStoragePath
{
    /// <summary>Runtime name, e.g. <c>vmhba0:C0:T0:L1</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The adapter the path leaves by, e.g. <c>vmhba0</c>.</summary>
    public string Adapter { get; init; } = string.Empty;

    /// <summary>active, standby, disabled, dead or unknown. Empty when unreported.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>The device's internal key, as the path table names it.</summary>
    public string DeviceKey { get; init; } = string.Empty;

    /// <summary>The device's NAA, when the second table could resolve it.</summary>
    public string StorageDeviceId { get; init; } = string.Empty;

    /// <summary>
    /// The <c>xsi:type</c> of the path's <c>transport</c>, e.g.
    /// <c>HostFibreChannelTargetTransport</c>; empty when none was reported.
    /// </summary>
    public string TransportType { get; init; } = string.Empty;

    /// <summary>
    /// The storage-side port this path lands on, or null when the transport
    /// does not name one.
    /// </summary>
    /// <remarks>
    /// Fibre Channel: the target's port WWN as sixteen lowercase hex digits
    /// in colon-separated pairs (<c>50:06:01:60:3b:20:1f:3a</c>), the form a
    /// switch or array reports it in, so the two can be joined later. iSCSI:
    /// the target's IQN. SAS and PCIe transports carry nothing that names a
    /// port (measured on a live vCenter), and they stay null rather than
    /// being given an invented identity.
    /// </remarks>
    public string? Target { get; init; }
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

    /// <summary>
    /// How many virtual processors it was given, or null when unreadable.
    /// </summary>
    /// <remarks>
    /// Requested for arithmetic rather than for display.
    /// <c>cpu.ready.summation</c> is summed across every vCPU, so the ready
    /// percentage anybody reasons about is the raw total divided by the
    /// interval <em>and</em> by this. Without it a rule either states a figure
    /// that is wrong by a factor of the vCPU count or declines to state one.
    /// </remarks>
    public int? VirtualCpuCount { get; init; }

    /// <summary>Configured memory in megabytes, or null when unreadable.</summary>
    public long? ConfiguredMemoryMb { get; init; }

    /// <summary>
    /// The configured CPU ceiling in MHz: null unreadable, -1 unlimited.
    /// </summary>
    /// <remarks>
    /// vCenter's own <c>-1</c> is kept rather than turned into null, because a
    /// machine with no limit and a machine whose configuration we may not read
    /// are different facts. See <see cref="Domain.EntitySizing.CpuLimitMhz"/>.
    /// </remarks>
    public long? CpuLimitMhz { get; init; }

    /// <summary>The configured memory ceiling in MB. See <see cref="CpuLimitMhz"/>.</summary>
    public long? MemoryLimitMb { get; init; }

    /// <summary>
    /// The snapshots it currently has, oldest first, flattened from the tree.
    /// </summary>
    /// <remarks>
    /// Empty both for a machine with no snapshots and for one whose snapshot
    /// property could not be read; the two are told apart by the read failures
    /// on the payload, as everywhere else here.
    /// </remarks>
    public IReadOnlyList<VsphereSnapshot> Snapshots { get; init; } = [];

    /// <summary>
    /// Bytes occupied by the snapshot chain, or null when it could not be computed.
    /// </summary>
    /// <remarks>
    /// Null and zero are different answers. Zero means the file layout was read
    /// and nothing in it belongs to a snapshot; null means the layout was not
    /// readable, and a machine whose size we could not compute must not be
    /// reported as one carrying nothing.
    /// </remarks>
    public long? SnapshotBytes { get; init; }
}

/// <summary>One snapshot, flattened out of the tree it arrived in.</summary>
/// <remarks>
/// The tree is kept only as far as <see cref="Depth"/>. What the failure mode
/// needs is the oldest creation time and how many there are; the parent/child
/// shape adds nothing an alert can act on, and flattening it means no consumer
/// has to walk a recursive structure to answer "how old is the oldest".
/// </remarks>
public sealed record VsphereSnapshot
{
    public required string Name { get; init; }

    /// <summary>Its managed object reference, e.g. <c>snapshot-2041</c>.</summary>
    public required string MoRef { get; init; }

    /// <summary>When it was taken, or null when vCenter's timestamp was unreadable.</summary>
    public DateTimeOffset? CreatedAtUtc { get; init; }

    /// <summary>How deep in the chain it sits; a root snapshot is 1.</summary>
    public int Depth { get; init; } = 1;
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

    /// <summary>
    /// The cluster's HA configuration, by the keys <see cref="ClusterHaSettings"/>
    /// declares -- admission control, host/VM monitoring, the APD/PDL response,
    /// the heartbeat datastore count and the <c>das.ignoreRedundantNetWarning</c>
    /// advanced option. Empty when <c>configurationEx.dasConfig</c> could not be
    /// read, exactly as <see cref="VsphereHost.AdvancedSettings"/> is empty for
    /// an unread host: a missing key is never the same fact as a reported empty
    /// one.
    /// </summary>
    public IReadOnlyDictionary<string, string> HaSettings { get; init; } = ClusterHaSettings.None;

    /// <summary>
    /// The VM and host groups <c>configurationEx.group</c> reports, named
    /// groups a DRS VM-host rule refers to by name.
    /// </summary>
    /// <remarks>
    /// Empty when the property could not be read, same contract as
    /// everywhere else in this record.
    /// </remarks>
    public IReadOnlyList<VsphereClusterGroup> Groups { get; init; } = [];

    /// <summary>
    /// The affinity, anti-affinity and VM-host rules <c>configurationEx.rule</c>
    /// reports.
    /// </summary>
    public IReadOnlyList<VsphereDrsRule> DrsRules { get; init; } = [];
}

/// <summary>
/// A named group of VMs or hosts, as vim25's <c>ClusterVmGroup</c> or
/// <c>ClusterHostGroup</c> reports it, one element of
/// <c>configurationEx.group</c>.
/// </summary>
/// <remarks>
/// The two vim25 types share nothing but the group name
/// (<c>ClusterGroupInfo.name</c>) and are told apart here by
/// <see cref="Kind"/> rather than by two separate wire types, because a DRS
/// VM-host rule names a VM group and a host group by name and a reader
/// resolving those references wants one lookup table, not two.
/// </remarks>
public sealed record VsphereClusterGroup
{
    public required string Name { get; init; }

    public required VsphereClusterGroupKind Kind { get; init; }

    /// <summary>
    /// The group's members: VM morefs for <see cref="VsphereClusterGroupKind.VirtualMachine"/>
    /// (vim25 <c>ClusterVmGroup.vm</c>), host morefs for
    /// <see cref="VsphereClusterGroupKind.Host"/> (vim25 <c>ClusterHostGroup.host</c>).
    /// </summary>
    public IReadOnlyList<string> MemberMoRefs { get; init; } = [];
}

/// <summary>Which vim25 group type a <see cref="VsphereClusterGroup"/> came from.</summary>
public enum VsphereClusterGroupKind
{
    VirtualMachine,
    Host,
}

/// <summary>
/// A DRS rule, as vim25's <c>ClusterAffinityRuleSpec</c>,
/// <c>ClusterAntiAffinityRuleSpec</c> or <c>ClusterVmHostRuleInfo</c> reports
/// it, one element of <c>configurationEx.rule</c>, close to the wire.
/// </summary>
/// <remarks>
/// Group references are carried as the names vCenter used
/// (<c>vmGroupName</c>, <c>affineHostGroupName</c>, <c>antiAffineHostGroupName</c>)
/// rather than resolved here: resolving a name against
/// <see cref="VsphereCluster.Groups"/> and turning a moref into an
/// <see cref="Domain.EntityId"/> is the source's job, the same split
/// <see cref="VsphereStoragePath"/> and the domain's <c>StoragePath</c> make.
/// </remarks>
public sealed record VsphereDrsRule
{
    public required string Name { get; init; }

    public required Domain.DrsRuleKind Kind { get; init; }

    /// <summary>vim25 <c>enabled</c>.</summary>
    public bool Enabled { get; init; }

    /// <summary>vim25 <c>mandatory</c>.</summary>
    public bool Mandatory { get; init; }

    /// <summary>vim25 <c>inCompliance</c>, or null when not reported.</summary>
    public bool? InCompliance { get; init; }

    /// <summary>
    /// VM morefs, directly from vim25 <c>vm</c>. Only set for
    /// <see cref="Domain.DrsRuleKind.Affinity"/> and
    /// <see cref="Domain.DrsRuleKind.AntiAffinity"/>.
    /// </summary>
    public IReadOnlyList<string> VirtualMachineMoRefs { get; init; } = [];

    /// <summary>
    /// vim25 <c>ClusterVmHostRuleInfo.vmGroupName</c>. Only set for the
    /// VM-host rule kinds.
    /// </summary>
    public string? VmGroupName { get; init; }

    /// <summary>
    /// vim25 <c>ClusterVmHostRuleInfo.affineHostGroupName</c> or
    /// <c>antiAffineHostGroupName</c>, whichever <see cref="Kind"/> selects.
    /// Only set for the VM-host rule kinds.
    /// </summary>
    public string? HostGroupName { get; init; }
}

/// <summary>A datastore as vCenter sees it.</summary>
public sealed record VsphereDatastore
{
    public required string MoRef { get; init; }

    public required string Name { get; init; }

    public long? CapacityBytes { get; init; }

    public long? FreeSpaceBytes { get; init; }

    /// <summary>
    /// Space promised to thin disks that has not been taken yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one measure that warns long before a datastore fills, and the
    /// counter map says so in as many words. Fullness is a statement about
    /// today: at 84% nothing is wrong and nothing is said, and the alert
    /// arrives at 85% with however many days of headroom that happens to
    /// leave. Uncommitted space is a statement about what has already been
    /// promised — a volume 40% full whose thin disks are entitled to three
    /// times its capacity is going to fill, and the only question is when.
    /// </para>
    /// <para>
    /// Null when unreadable, and legitimately absent on a datastore with no
    /// thin provisioning at all. Neither is a zero we may assert.
    /// </para>
    /// </remarks>
    public long? UncommittedBytes { get; init; }

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
