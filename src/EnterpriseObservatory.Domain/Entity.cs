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
public readonly record struct EntityId(string Value)
{
    public override string ToString() => Value;

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
}
