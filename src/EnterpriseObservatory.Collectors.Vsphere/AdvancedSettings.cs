namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// The host advanced settings this product carries, and their vSphere names.
/// </summary>
/// <remarks>
/// <para>
/// A host reports over a thousand advanced settings and this list is a few of
/// them. The filter is here rather than in a rule because the cost it avoids
/// is storage, not reasoning: everything kept is written every cycle for every
/// host, and a setting nothing asks about is a row nobody will ever read.
/// </para>
/// <para>
/// Every name here is taken from Broadcom's own Security Configuration Guide,
/// which publishes its controls as a versioned CSV in a public repository —
/// <c>vmware/vcf-security-and-compliance-guidelines</c>, release
/// <c>910-20260612-01</c> — with the parameter name stated per control. The
/// names are copied from that file rather than from memory or a blog, which is
/// the whole reason this list can be trusted to match what a host actually
/// reports.
/// </para>
/// <para>
/// **This list is not the compliance catalogue and must not grow into one.**
/// The guide carries 260 controls, of which roughly ninety are non-default on
/// ESX and vCenter, and the right home for them is a versioned table read as
/// data — §10 step 4. Hard-coding controls is precisely the mistake that left
/// vROps shipping alarms pinned to vSphere 5.5 in 2026: the controls were
/// compiled in as symptom definitions and there was no mechanism to swap the
/// benchmark. What belongs here is only what a shipped rule reads today.
/// </para>
/// </remarks>
public static class AdvancedSettings
{
    /// <summary>Where a host sends its logs, or empty when nobody set one.</summary>
    /// <remarks>
    /// Control <c>esx-9.log-forwarding</c>, priority P0, and marked non-default
    /// — the shipped value is empty, so an untouched host fails it.
    /// </remarks>
    public const string RemoteSyslogHost = "Syslog.global.logHost";

    /// <summary>Where the host writes logs locally.</summary>
    /// <remarks>
    /// Read alongside <see cref="RemoteSyslogHost"/> because the two together
    /// decide whether anything survives the host: a scratch-backed log
    /// directory on a stateless host is as good as no log at all.
    /// </remarks>
    public const string LocalLogDirectory = "Syslog.global.logDir";

    /// <summary>Whether audit records are forwarded off the host.</summary>
    /// <remarks>Control <c>esx-9.log-audit-forwarding</c>, P0.</remarks>
    public const string AuditRecordRemote = "Syslog.global.auditRecord.remoteEnable";

    /// <summary>Whether audit records are stored at all.</summary>
    /// <remarks>Control <c>esx-9.log-audit-local</c>, P0.</remarks>
    public const string AuditRecordStorage = "Syslog.global.auditRecord.storageEnable";

    /// <summary>Idle timeout for a shell session, in seconds.</summary>
    /// <remarks>Control <c>esx-9.shell-timeout</c>, P0.</remarks>
    public const string ShellTimeout = "UserVars.ESXiShellTimeOut";

    /// <summary>Idle timeout for an interactive shell, in seconds.</summary>
    /// <remarks>Control <c>esx-9.shell-interactive-timeout</c>, P0.</remarks>
    public const string ShellInteractiveTimeout = "UserVars.ESXiShellInteractiveTimeOut";

    /// <summary>The Active Directory group granted host administrator rights.</summary>
    /// <remarks>
    /// Control <c>esx-9.ad-admin-group-name</c>. The finding is that it is left
    /// at the documented default, which anyone can create in a domain.
    /// </remarks>
    public const string ActiveDirectoryAdminGroup =
        "Config.HostAgent.plugins.hostsvc.esxAdminsGroup";

    /// <summary>Whether page sharing works between virtual machines.</summary>
    /// <remarks>
    /// Not a security control and not a finding — it is an input to arithmetic.
    /// Inter-VM transparent page sharing has been off by default since ESXi 6.0
    /// and the shipped salting value makes each machine's salt unique, so any
    /// memory-overcommit reasoning calibrated on cross-VM deduplication has
    /// been wrong for a decade. Collected so a future rule can read the host's
    /// actual setting rather than assume the default, in either direction.
    /// </remarks>
    public const string PageSharingSalting = "Mem.ShareForceSalting";

    /// <summary>The names kept, compared without regard to case.</summary>
    /// <remarks>
    /// vSphere reports these names with stable casing, but the comparison is
    /// case-insensitive anyway: a key that differs only in case would silently
    /// drop the setting, and the failure would look exactly like a host that
    /// does not report it.
    /// </remarks>
    public static IReadOnlySet<string> Wanted { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RemoteSyslogHost,
            LocalLogDirectory,
            AuditRecordRemote,
            AuditRecordStorage,
            ShellTimeout,
            ShellInteractiveTimeout,
            ActiveDirectoryAdminGroup,
            PageSharingSalting,
        };

    /// <summary>What a host with no readable settings carries.</summary>
    public static IReadOnlyDictionary<string, string> None { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
