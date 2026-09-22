namespace EnterpriseObservatory.Domain;

/// <summary>
/// What kind of thing an entity is.
/// </summary>
/// <remarks>
/// Collectors may only produce entities of kinds their contract allows. A BMC
/// collector cannot produce an <see cref="EsxiHost"/> — the host list has
/// exactly one source of truth (vCenter). See ADR-0005 and product principle 2.
/// </remarks>
public enum EntityKind
{
    Unknown = 0,

    // --- hypervisor layer ---
    VCenter,
    Cluster,
    EsxiHost,
    VirtualMachine,
    Datastore,
    ResourcePool,

    // --- physical layer ---
    PhysicalServer,
    Bmc,
    HardwareComponent,
    HbaPort,

    // --- fabric layer ---
    SanSwitch,
    SanSwitchPort,

    // --- storage layer ---
    StorageArray,
    ArrayPort,
    Lun,

    // --- management plane ---
    ManagementAppliance,

    // --- the monitoring system observing itself ---
    CollectorInstance,
}

/// <summary>
/// A stable identifier for an entity within this installation.
/// </summary>
/// <remarks>
/// Opaque by design. It is not a vCenter MoRef, not an IP, not a serial — those
/// are <see cref="IdentityMark"/>s, which are evidence about an entity rather
/// than its identity. Several marks from several collectors may resolve to one
/// <see cref="EntityId"/>; that resolution is the job of the identity resolver,
/// never of a collector. See ADR-0003.
/// </remarks>
public readonly record struct EntityId
{
    /// <summary>
    /// Separates the source from the identifier it gave the thing.
    /// </summary>
    /// <remarks>
    /// A colon rather than a slash. Managed object references are unique within
    /// a vCenter but not between them, so an id has to carry its source — and
    /// an id ends up in a URL path, where a slash silently becomes an extra
    /// segment. That is not a hypothetical: it made every entity page in the
    /// product return the wrong thing, and with a catch-all route in front of
    /// it the failure was a 200 carrying HTML rather than an honest 404.
    /// </remarks>
    public const char Separator = ':';

    public EntityId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Contains('/', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal))
        {
            // Rejected here rather than escaped at each boundary. An identifier
            // that is safe everywhere is one fewer thing every caller has to
            // remember, and the caller who forgets is the one who finds out in
            // production.
            throw new ArgumentException(
                $"An entity id must not contain a path separator: '{value}'. " +
                $"Compose one with {nameof(EntityId)}.{nameof(For)}.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    /// <summary>Composes the id of something a source reported.</summary>
    /// <param name="source">The collector instance, e.g. a particular vCenter.</param>
    /// <param name="localId">What that source calls it.</param>
    public static EntityId For(string source, string localId) =>
        new($"{source}{Separator}{localId}");

    public static EntityId New() => new(Guid.NewGuid().ToString("n"));
}

/// <summary>
/// A single piece of evidence about which real-world thing an entity is.
/// </summary>
/// <param name="Kind">What sort of evidence this is.</param>
/// <param name="Value">The evidence itself, already normalized.</param>
/// <param name="Source">Which collector observed it, for auditing a match.</param>
/// <remarks>
/// The same physical server appears as an FQDN in vCenter, a hostname in iLO
/// and a serial number in OneView. Marks are how those observations are
/// recorded before anyone decides they are the same box.
/// </remarks>
public readonly record struct IdentityMark(IdentityMarkKind Kind, string Value, string Source)
{
    /// <summary>
    /// Normalizes an observed value so that trivially different spellings of
    /// the same evidence compare equal (case, surrounding whitespace).
    /// </summary>
    public static IdentityMark Create(IdentityMarkKind kind, string value, string source) =>
        new(kind, value.Trim().ToLowerInvariant(), source);
}

/// <summary>
/// The sort of evidence an <see cref="IdentityMark"/> carries, ordered by how
/// strongly it implies identity.
/// </summary>
public enum IdentityMarkKind
{
    /// <summary>Hardware UUID / SMBIOS UUID. Strongest available evidence.</summary>
    HardwareUuid = 0,

    /// <summary>Chassis or board serial number.</summary>
    SerialNumber = 1,

    /// <summary>Vendor service tag (Dell).</summary>
    ServiceTag = 2,

    /// <summary>A management or data-plane IP address.</summary>
    IpAddress = 3,

    /// <summary>Fully qualified domain name.</summary>
    Fqdn = 4,

    /// <summary>Short hostname, without domain.</summary>
    ShortHostname = 5,

    /// <summary>World-wide name of an HBA or array port.</summary>
    WorldWideName = 6,

    /// <summary>
    /// A storage volume's own identifier — a VMFS UUID, or an NFS one.
    /// </summary>
    /// <remarks>
    /// Identity evidence in the ordinary sense, and also the join that makes
    /// per-datastore performance possible at all: vSphere measures datastore
    /// latency on each host and names the volume in the counter instance, so
    /// this is what turns a number measured on a host into a number about a
    /// datastore. It is expected to be the thing an array also knows the LUN
    /// by, which is how the storage layer will attach to this one later.
    /// </remarks>
    VolumeIdentifier = 7,

    /// <summary>
    /// The storage device a volume sits on — a LUN's NAA identifier.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="VolumeIdentifier"/> and the distinction is the
    /// point. A VMFS volume has its own UUID and lives on one or more LUNs,
    /// each with an NAA of its own, and the two vocabularies do not overlap:
    /// vSphere names a datastore by the first and its storage paths and disk
    /// devices by the second. Holding both is what joins "this datastore is
    /// slow" to "this path has errors", and it is the same identifier a
    /// storage array will present the LUN under.
    /// </remarks>
    StorageDeviceId = 8,
}

/// <summary>
/// A thing we monitor.
/// </summary>
/// <remarks>
/// Entities are the nodes of the topology graph; metrics hang off them rather
/// than existing on their own. See ADR-0003.
/// </remarks>
public sealed record Entity
{
    public required EntityId Id { get; init; }

    public required EntityKind Kind { get; init; }

    /// <summary>Human-facing name. Display only — never used for matching.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Evidence about which real-world thing this is.</summary>
    public IReadOnlyList<IdentityMark> Marks { get; init; } = [];

    /// <summary>
    /// Which collector instance reported this.
    /// </summary>
    /// <remarks>
    /// Provenance, and load-bearing: an entity may only be treated as vanished
    /// by the source responsible for it. When a collector cannot be reached at
    /// all, everything it reports on is unknown, not gone — see
    /// <see cref="EntityGraph"/>.
    /// </remarks>
    public string SourceInstanceId { get; init; } = string.Empty;

    public HealthState Health { get; init; } = HealthState.Unknown;

    public ObservationState ObservationState { get; init; } = ObservationState.Active;

    /// <summary>When a collector last reported seeing this entity.</summary>
    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>
    /// Health as it should be reported, rather than as last observed.
    /// </summary>
    /// <remarks>
    /// An entity we cannot currently see has unknown health regardless of what
    /// it looked like when we last saw it. Reporting the stale value would be
    /// exactly the "showing green while blind" failure principle 1 forbids.
    /// </remarks>
    public HealthState EffectiveHealth =>
        ObservationState == ObservationState.Vanished ? HealthState.Unknown : Health;

    /// <summary>
    /// What this entity was configured with, when a collector could read it.
    /// </summary>
    /// <remarks>
    /// Null when nothing was read — an entity kind that has no sizing, or one
    /// whose configuration the account may not see. Never a zero standing in
    /// for "we did not look".
    /// </remarks>
    public EntitySizing? Sizing { get; init; }

    /// <summary>
    /// The storage paths this entity reaches its devices by.
    /// </summary>
    /// <remarks>
    /// Empty for everything that is not a host, and for a host whose path table
    /// could not be read. See <see cref="StoragePath"/> for why it is here at
    /// all rather than being derivable from the counters.
    /// </remarks>
    public IReadOnlyList<StoragePath> StoragePaths { get; init; } = [];

    /// <summary>
    /// Configuration settings read from the entity, by their vendor name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// State rather than measurement, and that is what makes it belong here
    /// instead of in a series. "What is this set to" has one answer that stays
    /// true until somebody changes it; writing it every twenty seconds would
    /// record thousands of times a day that nothing happened.
    /// </para>
    /// <para>
    /// **A missing key is not an empty value.** A setting nobody configured
    /// arrives as an empty string; a setting the product could not read is
    /// simply absent. Only the first is a finding, and a rule that treats them
    /// alike accuses a host it never managed to look at. This is the same
    /// distinction <see cref="EntitySizing.VirtualCpuCount"/> makes with null,
    /// and it is made the same way for the same reason.
    /// </para>
    /// <para>
    /// Names are the vendor's own, not translated. A translation layer here
    /// would need a second vendor to be designed against, and inventing one
    /// from a single example is how a mapping ends up shaped like vSphere
    /// wearing a neutral label.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // The four below are typed properties rather than more Settings keys:
    // services, time and switch policies are lists of structures with a
    // three-valued "unset" inside them, and flattening them into a string
    // dictionary would mean inventing a key syntax and losing that third value.
    // Every one is null when it was not read, which is not the same as empty.

    /// <summary>
    /// The services this host reported, or null when they were not read.
    /// </summary>
    /// <remarks>
    /// Null is "the product could not see", empty is "the host has none".
    /// A rule must stay quiet on the first and may speak on the second.
    /// </remarks>
    public IReadOnlyList<HostService>? Services { get; init; }

    /// <summary>How this host keeps time, or null when that was not read.</summary>
    public TimeConfiguration? TimeConfiguration { get; init; }

    /// <summary>
    /// The security policy of each standard vSwitch, or null when not read.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="PortGroupSecurity"/> because the two are read
    /// separately and can fail separately; one list could not say which half
    /// was missing.
    /// </remarks>
    public IReadOnlyList<NetworkSecurityPolicy>? VirtualSwitchSecurity { get; init; }

    /// <summary>
    /// The security policy of each standard port group, or null when not read.
    /// </summary>
    public IReadOnlyList<NetworkSecurityPolicy>? PortGroupSecurity { get; init; }

    /// <summary>
    /// The host's lockdown mode in the platform's words, or null when not read.
    /// </summary>
    /// <remarks>
    /// <c>lockdownDisabled</c>, <c>lockdownNormal</c> or <c>lockdownStrict</c>.
    /// Carried as the platform words it, like <see cref="Settings"/>; the
    /// legacy two-valued <c>adminDisabled</c> flag is deliberately not read,
    /// because it cannot tell normal from strict.
    /// </remarks>
    public string? LockdownMode { get; init; }

    /// <summary>
    /// The DRS affinity, anti-affinity and VM-host rules configured on this
    /// cluster, resolved to member entity ids.
    /// </summary>
    /// <remarks>
    /// Empty for everything that is not a cluster, and for a cluster whose
    /// rule table could not be read. See <see cref="DrsRule"/> for why the
    /// group references are resolved here rather than left as group names —
    /// same contract as <see cref="StoragePaths"/>: carried, not judged.
    /// </remarks>
    public IReadOnlyList<DrsRule> DrsRules { get; init; } = [];
}

/// <summary>Which shape of DRS rule this is.</summary>
/// <remarks>
/// vim25 models these as four sibling types of <c>ClusterRuleInfo</c>:
/// <c>ClusterAffinityRuleSpec</c>, <c>ClusterAntiAffinityRuleSpec</c> and
/// <c>ClusterVmHostRuleInfo</c> (which carries either
/// <c>affineHostGroupName</c> or <c>antiAffineHostGroupName</c>, never both).
/// See developer.broadcom.com, vSphere Web Services API reference,
/// <c>vim.cluster.RuleInfo</c> and its subtypes.
/// </remarks>
public enum DrsRuleKind
{
    /// <summary>VMs should run on the same host. <c>ClusterAffinityRuleSpec</c>.</summary>
    Affinity,

    /// <summary>VMs should run on different hosts. <c>ClusterAntiAffinityRuleSpec</c>.</summary>
    AntiAffinity,

    /// <summary>
    /// A VM group's members must/should run on a host group's members.
    /// <c>ClusterVmHostRuleInfo.affineHostGroupName</c>.
    /// </summary>
    VmHostAffine,

    /// <summary>
    /// A VM group's members must/should not run on a host group's members.
    /// <c>ClusterVmHostRuleInfo.antiAffineHostGroupName</c>.
    /// </summary>
    VmHostAntiAffine,
}

/// <summary>
/// One DRS rule, as configured on a cluster, with its groups already resolved.
/// </summary>
/// <remarks>
/// <para>
/// Carried on the cluster entity rather than as a separate entity kind: a
/// rule has no identity of its own worth resolving across cycles beyond its
/// name, which vCenter already guarantees unique within one cluster, and it
/// changes at the same rhythm as the cluster's other configuration.
/// </para>
/// <para>
/// <see cref="VirtualMachineEntityIds"/> and <see cref="HostEntityIds"/> hold
/// already-resolved <see cref="EntityId.Value"/>s, not the bare vim25 managed
/// object references the wire carries: resolving them needs the source's
/// instance id, which is the collector's business, so a rule stored here has
/// already been through that step -- unlike, for example,
/// <see cref="StoragePath.StorageDeviceId"/>, which stays wire-shaped because
/// it identifies hardware rather than another entity.
/// </para>
/// </remarks>
public sealed record DrsRule
{
    /// <summary>The rule's name, unique within its cluster. vim25 <c>name</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// vim25 <c>ruleUuid</c>, or null when vCenter did not report one.
    /// </summary>
    /// <remarks>
    /// The rule's identity for a continuity finding: a renamed rule keeps its
    /// uuid, so the finding and its acceptance survive the rename. The name
    /// stands in only when no uuid was reported.
    /// </remarks>
    public string? RuleUuid { get; init; }

    public required DrsRuleKind Kind { get; init; }

    /// <summary>vim25 <c>enabled</c>. A disabled rule is not enforced by DRS.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// vim25 <c>mandatory</c>: compliance is required, not merely preferred.
    /// </summary>
    public bool Mandatory { get; init; }

    /// <summary>
    /// vCenter's own verdict, or null when it did not report one.
    /// </summary>
    /// <remarks>
    /// vim25 <c>inCompliance</c>: "Flag to indicate whether or not the
    /// placement of Virtual Machines is currently in compliance with this
    /// rule." Carried so a rule evaluated from placement can say when it
    /// agrees with vCenter's own judgement and when it does not, rather than
    /// silently overwriting one verdict with the other.
    /// </remarks>
    public bool? VCenterInCompliance { get; init; }

    /// <summary>
    /// The VMs this rule concerns, as the <see cref="EntityId.Value"/> each
    /// resolves to — already qualified with the reporting source, unlike the
    /// bare vim25 managed object references a collector reads off the wire.
    /// </summary>
    /// <remarks>
    /// For <see cref="DrsRuleKind.Affinity"/> and
    /// <see cref="DrsRuleKind.AntiAffinity"/>, vim25 <c>vm</c> directly. For
    /// the VM-host kinds, the members of the VM group vim25 <c>vmGroupName</c>
    /// names, resolved from <c>configurationEx.group</c>.
    /// </remarks>
    public IReadOnlyList<string> VirtualMachineEntityIds { get; init; } = [];

    /// <summary>
    /// The hosts this rule concerns, as qualified entity id values — see
    /// <see cref="VirtualMachineEntityIds"/>. Empty for
    /// <see cref="DrsRuleKind.Affinity"/> and <see cref="DrsRuleKind.AntiAffinity"/>,
    /// which name no host group.
    /// </summary>
    /// <remarks>
    /// The members of the host group vim25 names as
    /// <c>affineHostGroupName</c> or <c>antiAffineHostGroupName</c>, depending
    /// on <see cref="Kind"/>, resolved from <c>configurationEx.group</c>.
    /// </remarks>
    public IReadOnlyList<string> HostEntityIds { get; init; } = [];
}

/// <summary>
/// The sizes and limits an entity was configured with, as opposed to what it
/// is using.
/// </summary>
/// <remarks>
/// <para>
/// Not identity evidence, and deliberately not an <see cref="IdentityMark"/>.
/// Marks are weighed by the resolver to decide whether two records are the
/// same machine, and "4 vCPUs" is true of several thousand virtual machines in
/// an ordinary estate. It is the same worthless evidence as the short hostname
/// <c>10</c> derived from <c>10.5.1.76</c>, which this product has already been
/// bitten by; adding it as a mark would not merely be untidy, it would make
/// identity resolution worse.
/// </para>
/// <para>
/// Not a series either. These change when somebody reconfigures a machine,
/// perhaps twice a year, and storing a configuration fact at the metric rhythm
/// would write thousands of identical rows a day to record something that did
/// not happen.
/// </para>
/// <para>
/// So: a property of the entity. It is read on the inventory rhythm and it is
/// what a rule divides by — the whole reason it has to be reachable rather
/// than merely collected.
/// </para>
/// </remarks>
public sealed record EntitySizing
{
    /// <summary>
    /// How many virtual processors the machine was given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The missing half of CPU ready time.
    /// <see cref="CounterValue.AsPercentageOfInterval"/> divides a summed
    /// millisecond count by the interval it was accumulated over, which is
    /// correct for a single processor and wrong by a factor of this number for
    /// anything else: vSphere sums ready time across every vCPU, so an
    /// eight-way machine at a genuinely healthy 2% reads as 16% and an
    /// operator is sent to fix a machine that is fine.
    /// </para>
    /// <para>
    /// Null when it could not be read. A rule must then decline to state a
    /// ready percentage rather than assume one processor.
    /// </para>
    /// </remarks>
    public int? VirtualCpuCount { get; init; }

    /// <summary>Configured memory in megabytes, or null when unreadable.</summary>
    public long? ConfiguredMemoryMb { get; init; }

    /// <summary>
    /// A configured CPU ceiling in MHz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three states, and all three are needed. Null means the property was not
    /// readable. <c>-1</c> is the platform's own word for "no limit" and is
    /// kept as it arrived rather than folded into null, because "unlimited"
    /// and "we could not look" lead to opposite conclusions. A positive value
    /// is a real ceiling.
    /// </para>
    /// <para>
    /// It matters because a throttled machine is indistinguishable from a
    /// contended one from the guest's side: both wait, both look slow, and the
    /// host is perfectly healthy in the first case. A contention rule without
    /// this will confidently blame the wrong thing.
    /// </para>
    /// </remarks>
    public long? CpuLimitMhz { get; init; }

    /// <summary>A configured memory ceiling in megabytes. See <see cref="CpuLimitMhz"/>.</summary>
    public long? MemoryLimitMb { get; init; }

    /// <summary>Whether a real CPU ceiling is configured, as opposed to absent or unreadable.</summary>
    public bool IsCpuLimited => CpuLimitMhz is > 0;

    /// <summary>Whether a real memory ceiling is configured.</summary>
    public bool IsMemoryLimited => MemoryLimitMb is > 0;
}

/// <summary>
/// One route from a host to one storage device.
/// </summary>
/// <remarks>
/// <para>
/// Two facts in one row, and the product needs both.
/// </para>
/// <para>
/// The first is redundancy. A storage device is reached over several paths so
/// that losing one costs nothing, which is exactly why losing one is silent:
/// nothing degrades, no counter moves, and the estate stays that way until the
/// second path dies and every machine on the volume stops at once. Counting
/// paths and their states is the only way to see the first failure.
/// </para>
/// <para>
/// The second is attribution. Path fault counters name the path by its runtime
/// name — <c>vmhba0:C0:T0:L1</c> — which carries a bus, a target and a LUN
/// number but no LUN <em>identity</em>. A datastore is known by its VMFS UUID
/// and, through <see cref="IdentityMarkKind.StorageDeviceId"/>, by the NAA of
/// the device it sits on. This row is what joins the two: runtime name here,
/// NAA in <see cref="StorageDeviceId"/>, and the datastore carrying the same
/// NAA as a mark. Without it a bus reset can be attributed to a host and an
/// HBA but never to the datastore it took down.
/// </para>
/// </remarks>
public sealed record StoragePath
{
    /// <summary>The runtime name, e.g. <c>vmhba0:C0:T0:L1</c>.</summary>
    /// <remarks>
    /// Unique within a host and not between hosts, which is why this is a
    /// property of one entity rather than a mark that a resolver might match
    /// two hosts on.
    /// </remarks>
    public required string Name { get; init; }

    /// <summary>
    /// The device this path leads to, as an NAA, or empty when unresolved.
    /// </summary>
    /// <remarks>
    /// Empty rather than guessed. The path table names its device by an
    /// internal key, and turning that into the NAA every other part of the
    /// product speaks needs a second table; when that lookup fails the path is
    /// still reported, because "this path is dead" is worth saying even when
    /// nobody can say which LUN it led to.
    /// </remarks>
    public string StorageDeviceId { get; init; } = string.Empty;

    /// <summary>
    /// The platform's own key for the device, when it has one.
    /// </summary>
    /// <remarks>
    /// Carried because redundancy has to be countable even when
    /// <see cref="StorageDeviceId"/> could not be resolved. Grouping paths by
    /// an empty NAA would pile every unnamed device in the host into one heap
    /// and report a single enormous set of paths where there are a dozen small
    /// ones — and the case where the name is missing is not the case in which
    /// to give up on counting.
    /// </remarks>
    public string DeviceKey { get; init; } = string.Empty;

    /// <summary>
    /// The path's state, as the platform words it: active, standby, disabled,
    /// dead or unknown. Empty when it was not reported.
    /// </summary>
    public string State { get; init; } = string.Empty;

    /// <summary>
    /// The adapter this path leaves the host by, e.g. <c>vmhba0</c>.
    /// </summary>
    /// <remarks>
    /// Carried because it is what somebody physically goes and looks at. Four
    /// dead paths on one adapter is a cable or an SFP; four dead paths spread
    /// across four adapters is the array.
    /// </remarks>
    public string Adapter { get; init; } = string.Empty;

    /// <summary>
    /// How the path reaches its storage, as the platform words it, e.g.
    /// <c>HostFibreChannelTargetTransport</c>. Empty when not reported.
    /// </summary>
    public string Transport { get; init; } = string.Empty;

    /// <summary>
    /// The storage-side port this path lands on, or null when unknown.
    /// </summary>
    /// <remarks>
    /// A Fibre Channel target port WWN as <c>50:06:01:60:3b:20:1f:3a</c>, the
    /// form a switch or array reports it in, or an iSCSI target IQN. Null
    /// means the transport names no port (SAS, PCIe) or was not reported —
    /// unknown, which a rule must not read as "the same target as the rest".
    /// </remarks>
    public string? Target { get; init; }

    /// <summary>
    /// Whether the platform reports this path as dead.
    /// </summary>
    /// <remarks>
    /// Only <c>dead</c> is dead. A standby path in an ALUA configuration is
    /// working and unused, and counting it as lost would raise a redundancy
    /// alert on every correctly configured array in the estate.
    /// </remarks>
    public bool IsDead => string.Equals(State, "dead", StringComparison.OrdinalIgnoreCase);
}
