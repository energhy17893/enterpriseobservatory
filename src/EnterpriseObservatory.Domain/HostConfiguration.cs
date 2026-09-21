namespace EnterpriseObservatory.Domain;

/// <summary>
/// One service a host runs or can run, as the platform reports it.
/// </summary>
/// <remarks>
/// <para>
/// Carried, not judged. Whether SSH running is a finding depends on a control
/// and on an exception somebody may have granted, and both belong to the
/// compliance engine; the collector owes it the table.
/// </para>
/// <para>
/// <see cref="Running"/> and <see cref="Policy"/> are nullable although the
/// platform documents both as always present, because a value the product
/// could not read must not arrive looking like "stopped" or like a policy.
/// </para>
/// </remarks>
public sealed record HostService
{
    /// <summary>The platform's own key, e.g. <c>TSM-SSH</c>, <c>TSM</c>, <c>ntpd</c>.</summary>
    public required string Key { get; init; }

    /// <summary>The human-facing label, e.g. <c>SSH</c>. Display only.</summary>
    public string? Label { get; init; }

    /// <summary>Whether it is running now, or null when that was not reported.</summary>
    public bool? Running { get; init; }

    /// <summary>
    /// When it starts, in the platform's words: <c>on</c>, <c>off</c> or
    /// <c>automatic</c>. Null when not reported.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Running"/> because the two disagree in the
    /// case that matters: SSH switched on by hand with policy <c>off</c> is
    /// running now and gone after a reboot, which is a different finding from
    /// SSH set to start with the host.
    /// </remarks>
    public string? Policy { get; init; }
}

/// <summary>
/// How a host keeps its clock, as the platform reports it.
/// </summary>
/// <remarks>
/// <para>
/// Both protocols are carried because a host may use either. A rule that
/// looked only at NTP servers would report every PTP host as having no time
/// source, which is the product being confidently wrong about a host that is
/// configured correctly.
/// </para>
/// <para>
/// The whole record is null on the entity when the time configuration was not
/// read at all. Inside a record that exists, the configuration <em>was</em>
/// read, so an absent part is the host's answer: no NTP configuration is an
/// empty <see cref="NtpServers"/>, not a null one.
/// </para>
/// </remarks>
public sealed record TimeConfiguration
{
    /// <summary>
    /// Which protocol disciplines the clock: <c>ntp</c> or <c>ptp</c>.
    /// </summary>
    /// <remarks>
    /// Null on hosts older than vSphere 7.0 Update 3, which do not report it
    /// and can only use NTP. Null is left as null rather than filled in as
    /// <c>ntp</c>: inferring it from a version is a rule's decision.
    /// </remarks>
    public string? Protocol { get; init; }

    /// <summary>
    /// Whether the time service is enabled, when the host reports it.
    /// </summary>
    /// <remarks>Reported only by recent hosts; null otherwise.</remarks>
    public bool? Enabled { get; init; }

    /// <summary>The NTP servers configured, in order. Empty when none are.</summary>
    public IReadOnlyList<string> NtpServers { get; init; } = [];

    /// <summary>The PTP configuration, or null when the host has none.</summary>
    public PtpConfiguration? Ptp { get; init; }
}

/// <summary>A host's Precision Time Protocol configuration.</summary>
public sealed record PtpConfiguration
{
    /// <summary>The PTP domain number, or null when not reported.</summary>
    public int? Domain { get; init; }

    /// <summary>The devices PTP runs on, e.g. <c>vmk0</c> or a PCI passthrough NIC.</summary>
    public IReadOnlyList<string> PortDevices { get; init; } = [];
}

/// <summary>
/// The three layer-2 security switches, as one level of the platform set them.
/// </summary>
/// <remarks>
/// Every value is nullable and null is meaningful. On a port group, an absent
/// value means "inherited from the switch", not "false" — and false is the
/// secure answer for all three, so collapsing absent into false would report
/// a port group that inherits <c>accept</c> as locked down.
/// </remarks>
public sealed record SecurityPolicyFlags
{
    public bool? AllowPromiscuous { get; init; }

    public bool? MacChanges { get; init; }

    public bool? ForgedTransmits { get; init; }
}

/// <summary>Where a network security policy is set.</summary>
public enum NetworkPolicyScope
{
    Unknown = 0,

    /// <summary>A standard virtual switch.</summary>
    VirtualSwitch,

    /// <summary>A port group on a standard virtual switch.</summary>
    PortGroup,
}

/// <summary>
/// The security policy of one standard vSwitch or port group.
/// </summary>
/// <remarks>
/// <para>
/// Two readings, because a rule needs both. <see cref="Configured"/> is what
/// was set at this level, with inheritance left as null. <see cref="Effective"/>
/// is what the platform says actually applies — for a port group its computed
/// policy, for a switch the same as what was configured, since a switch
/// inherits from nothing. A rule asking "is promiscuous mode allowed here"
/// wants <see cref="Effective"/>; a rule asking "who overrode the switch"
/// wants <see cref="Configured"/>.
/// </para>
/// <para>
/// Standard switches only. A distributed switch is a vCenter object with its
/// own inventory and is not read here.
/// </para>
/// </remarks>
public sealed record NetworkSecurityPolicy
{
    public required NetworkPolicyScope Scope { get; init; }

    /// <summary>The switch or port group name, e.g. <c>vSwitch0</c> or <c>VM Network</c>.</summary>
    public required string Name { get; init; }

    /// <summary>For a port group, the switch it sits on; for a switch, its own name.</summary>
    public string? VirtualSwitchName { get; init; }

    /// <summary>What was set at this level. Null fields were not set here.</summary>
    public SecurityPolicyFlags Configured { get; init; } = new();

    /// <summary>What applies, or null when the platform did not say.</summary>
    public SecurityPolicyFlags? Effective { get; init; }
}
