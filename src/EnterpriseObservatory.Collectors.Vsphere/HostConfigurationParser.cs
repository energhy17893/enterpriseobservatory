using System.Globalization;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Reads the host configuration the compliance engine asks about: services,
/// time, standard switch security and lockdown mode.
/// </summary>
/// <remarks>
/// <para>
/// Carried, not judged. Nothing here decides whether SSH running is a problem;
/// it only makes sure that whatever does decide is handed what the host said,
/// and is never handed a default that looks like an answer.
/// </para>
/// <para>
/// One rule holds throughout: a property that was not reported comes back as
/// null, a property that was reported and empty comes back empty. The first
/// is the product failing to see; the second is the host's answer. The
/// coverage count and the payload's read failures say why a property is
/// missing; this only refuses to paper over it.
/// </para>
/// <para>
/// <strong>None of these shapes has been seen from a live vCenter.</strong>
/// They follow the published vim25 schema — <c>HostServiceInfo</c>,
/// <c>HostDateTimeInfo</c>, <c>HostVirtualSwitch</c>, <c>HostPortGroup</c> —
/// and the tests prove the reader against XML in that shape, not the shape
/// itself. The one structure in this collector that was dumped from a real
/// server did not match what its name implied, so treat these as a reasonable
/// reading until somebody points them at one.
/// </para>
/// </remarks>
public static class HostConfigurationParser
{
    public const string ServicesPath = "config.service";
    public const string DateTimePath = "config.dateTimeInfo";
    public const string VirtualSwitchPath = "config.network.vswitch";
    public const string PortGroupPath = "config.network.portgroup";
    public const string LockdownModePath = "config.lockdownMode";

    /// <summary>The host's services, or null when <c>config.service</c> was not reported.</summary>
    /// <remarks>
    /// <c>config.service</c> is a <c>HostServiceInfo</c> whose <c>service</c>
    /// children are each a <c>HostService</c>: <c>key</c>, <c>label</c>,
    /// <c>running</c>, <c>policy</c> and some packaging detail nothing reads.
    /// </remarks>
    public static IReadOnlyList<HostService>? ReadServices(PropertyObject host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (Nodes(host, ServicesPath) is not { } nodes)
        {
            return null;
        }

        var services = new List<HostService>();

        foreach (var service in nodes.Where(n => Is(n, "service")))
        {
            var key = service.TextOf("key");
            if (key.Length == 0)
            {
                // A service with no key cannot be named by any control, and
                // storing it under nothing would be a row nobody can ask for.
                continue;
            }

            services.Add(new HostService
            {
                Key = key,
                Label = NonEmpty(service.TextOf("label")),
                Running = Boolean(service.TextOf("running")),
                Policy = NonEmpty(service.TextOf("policy")),
            });
        }

        return services;
    }

    /// <summary>
    /// The host's time configuration, or null when <c>config.dateTimeInfo</c>
    /// was not reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HostDateTimeInfo</c> carries <c>timeZone</c>, <c>ntpConfig</c> (whose
    /// <c>server</c> children are the configured servers) and, from vSphere 7.0
    /// Update 3, <c>systemClockProtocol</c> and <c>ptpConfig</c>. Newer hosts
    /// may add <c>enabled</c>.
    /// </para>
    /// <para>
    /// Once the structure has arrived its parts are the host's answer: an
    /// absent <c>ntpConfig</c> is "no NTP servers", an absent
    /// <c>ptpConfig</c> is "no PTP". Only the whole being absent is "not read".
    /// </para>
    /// </remarks>
    public static TimeConfiguration? ReadTimeConfiguration(PropertyObject host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (Nodes(host, DateTimePath) is not { } nodes)
        {
            return null;
        }

        var ntp = nodes.FirstOrDefault(n => Is(n, "ntpConfig"));
        var ptp = nodes.FirstOrDefault(n => Is(n, "ptpConfig"));

        return new TimeConfiguration
        {
            Protocol = NonEmpty(TextAmong(nodes, "systemClockProtocol")),
            Enabled = Boolean(TextAmong(nodes, "enabled")),
            NtpServers = ntp is null
                ? []
                : [.. ntp.All("server").Select(s => s.Text).Where(s => s.Length > 0)],
            Ptp = ptp is null ? null : ReadPtp(ptp),
        };
    }

    private static PtpConfiguration ReadPtp(PropertyNode ptp) => new()
    {
        Domain = int.TryParse(
            ptp.TextOf("domain"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var domain)
            ? domain
            : null,
        // A port whose device type is "none" is a slot, not a configuration.
        PortDevices =
        [
            .. ptp.All("port")
                .Where(p => !string.Equals(p.TextOf("deviceType"), "none", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.TextOf("device"))
                .Where(d => d.Length > 0),
        ],
    };

    /// <summary>
    /// The security policy of each standard vSwitch, or null when
    /// <c>config.network.vswitch</c> was not reported.
    /// </summary>
    /// <remarks>
    /// Each element is a <c>HostVirtualSwitch</c> with a <c>name</c> and a
    /// <c>spec.policy.security</c>. A switch inherits from nothing, so what is
    /// configured is also what is effective.
    /// </remarks>
    public static IReadOnlyList<NetworkSecurityPolicy>? ReadVirtualSwitchSecurity(PropertyObject host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (Nodes(host, VirtualSwitchPath) is not { } nodes)
        {
            return null;
        }

        var policies = new List<NetworkSecurityPolicy>();

        foreach (var vswitch in nodes)
        {
            var name = vswitch.TextOf("name");
            if (name.Length == 0)
            {
                continue;
            }

            var configured = Flags(vswitch.Child("spec")?.Child("policy")?.Child("security"));

            policies.Add(new NetworkSecurityPolicy
            {
                Scope = NetworkPolicyScope.VirtualSwitch,
                Name = name,
                VirtualSwitchName = name,
                Configured = configured,
                Effective = configured,
            });
        }

        return policies;
    }

    /// <summary>
    /// The security policy of each standard port group, or null when
    /// <c>config.network.portgroup</c> was not reported.
    /// </summary>
    /// <remarks>
    /// Each element is a <c>HostPortGroup</c>: <c>spec</c> holds the name, the
    /// switch name and the policy set at this level, where an absent field
    /// means "inherit from the switch"; <c>computedPolicy</c> holds what the
    /// platform says actually applies. Both are carried so that inheritance is
    /// never resolved here by guesswork.
    /// </remarks>
    public static IReadOnlyList<NetworkSecurityPolicy>? ReadPortGroupSecurity(PropertyObject host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (Nodes(host, PortGroupPath) is not { } nodes)
        {
            return null;
        }

        var policies = new List<NetworkSecurityPolicy>();

        foreach (var portGroup in nodes)
        {
            var spec = portGroup.Child("spec");
            var name = spec?.TextOf("name") ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            var computed = portGroup.Child("computedPolicy")?.Child("security");

            policies.Add(new NetworkSecurityPolicy
            {
                Scope = NetworkPolicyScope.PortGroup,
                Name = name,
                VirtualSwitchName = NonEmpty(spec!.TextOf("vswitchName")),
                Configured = Flags(spec.Child("policy")?.Child("security")),
                Effective = computed is null ? null : Flags(computed),
            });
        }

        return policies;
    }

    /// <summary>
    /// <c>lockdownDisabled</c>, <c>lockdownNormal</c> or <c>lockdownStrict</c>,
    /// or null when not reported.
    /// </summary>
    public static string? ReadLockdownMode(PropertyObject host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return PropertyCollectorParser.ReadString(host.Values, LockdownModePath);
    }

    /// <summary>
    /// The top-level nodes of a structured property, or null when it was not
    /// reported in a readable shape.
    /// </summary>
    /// <remarks>
    /// The property parser files a structure whose children are all scalars,
    /// or an empty one, under values rather than structures. An empty value is
    /// a reported empty — no services, no switches — and is returned as no
    /// nodes. A non-empty value is a structure flattened into one string,
    /// which no reader here can honestly take apart, so it is refused as null
    /// rather than read as empty.
    /// </remarks>
    private static IReadOnlyList<PropertyNode>? Nodes(PropertyObject host, string path)
    {
        if (host.Structures.TryGetValue(path, out var nodes))
        {
            return nodes;
        }

        return host.Values.TryGetValue(path, out var flat) && flat.Length == 0 ? [] : null;
    }

    private static SecurityPolicyFlags Flags(PropertyNode? security) => security is null
        ? new SecurityPolicyFlags()
        : new SecurityPolicyFlags
        {
            AllowPromiscuous = Boolean(security.TextOf("allowPromiscuous")),
            MacChanges = Boolean(security.TextOf("macChanges")),
            ForgedTransmits = Boolean(security.TextOf("forgedTransmits")),
        };

    private static bool Is(PropertyNode node, string name) =>
        string.Equals(node.Name, name, StringComparison.Ordinal);

    private static string TextAmong(IReadOnlyList<PropertyNode> nodes, string name) =>
        nodes.FirstOrDefault(n => Is(n, name))?.Text ?? string.Empty;

    private static bool? Boolean(string text) => bool.TryParse(text, out var value) ? value : null;

    private static string? NonEmpty(string text) => text.Length == 0 ? null : text;
}
