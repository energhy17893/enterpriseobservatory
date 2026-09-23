using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Which HA-protected clusters are not actually protected.
/// </summary>
/// <remarks>
/// <para>
/// A cluster is added to vCenter with HA turned on once, at build time, and
/// then nobody looks at it again until a host dies and the workloads it held
/// do not come back. Every finding here is a setting that silently defeats
/// the reason HA exists while leaving the cluster looking green: HA itself
/// can be enabled while admission control is off, while host monitoring is
/// off, while the cluster has one heartbeat datastore instead of two, or
/// while vCenter's own warning about a non-redundant management network has
/// been switched off rather than fixed. None of these read as a health
/// problem on the cluster tile, because none of them are — until the day a
/// host fails and the promise the cluster was configured to keep is not the
/// one it can actually keep.
/// </para>
/// <para>
/// Reads <c>Entity.Settings</c> rather than a typed configuration object, for
/// the same reason the old remote-logging rule did. The setting names are
/// held as policy rather than compiled in, also for the reason
/// <c>RemoteLoggingPolicy</c> used to give: the application layer must not
/// depend on what vSphere calls something, so this file does not reference
/// the collector's <c>ClusterHaSettings</c> constants directly -- it carries
/// its own defaults, which the collector's constants are required to match.
/// </para>
/// <para>
/// Every check is silent on a cluster whose configuration could not be read.
/// An absent key is a collection failure with its own channel, not a finding
/// about a cluster nobody looked at -- the old remote-logging rule's remarks
/// covered why that distinction is load-bearing rather than a nicety.
/// </para>
/// </remarks>
public sealed record ClusterHighAvailabilityPolicy
{
    public static ClusterHighAvailabilityPolicy Default { get; } = new();

    /// <summary>Whether vSphere HA is enabled. <c>ClusterDasConfigInfo.enabled</c>.</summary>
    public string EnabledSetting { get; init; } = "dasConfig.enabled";

    /// <summary>
    /// Whether strict admission control is enabled.
    /// <c>ClusterDasConfigInfo.admissionControlEnabled</c>.
    /// </summary>
    public string AdmissionControlEnabledSetting { get; init; } = "dasConfig.admissionControlEnabled";

    /// <summary>
    /// <c>enabled</c> or <c>disabled</c>. <c>ClusterDasConfigInfo.hostMonitoring</c>.
    /// </summary>
    public string HostMonitoringSetting { get; init; } = "dasConfig.hostMonitoring";

    /// <summary>
    /// The cluster-wide default response to an All-Paths-Down storage
    /// failure. <c>ClusterDasConfigInfo.defaultVmSettings
    /// .vmComponentProtectionSettings.vmStorageProtectionForAPD</c>.
    /// </summary>
    public string ApdResponseSetting { get; init; } =
        "dasConfig.defaultVmSettings.vmComponentProtectionSettings.vmStorageProtectionForAPD";

    /// <summary>
    /// The cluster-wide default response to a Permanent-Device-Loss storage
    /// failure. <c>ClusterDasConfigInfo.defaultVmSettings
    /// .vmComponentProtectionSettings.vmStorageProtectionForPDL</c>.
    /// </summary>
    public string PdlResponseSetting { get; init; } =
        "dasConfig.defaultVmSettings.vmComponentProtectionSettings.vmStorageProtectionForPDL";

    /// <summary>
    /// How many datastores are configured for storage heartbeating, as a
    /// count rather than as the datastores themselves.
    /// </summary>
    public string HeartbeatDatastoreCountSetting { get; init; } = "dasConfig.heartbeatDatastore.count";

    /// <summary>
    /// How HA chooses heartbeat datastores (<c>hBDatastoreCandidatePolicy</c>).
    /// </summary>
    /// <remarks>
    /// <c>heartbeatDatastore</c> is the <b>user-preferred</b> list, not the set
    /// HA actually uses. Under <c>allFeasibleDs</c> or
    /// <c>allFeasibleDsWithUserPreference</c> (the default) HA picks two by
    /// itself, so an empty or short preferred list is normal. Only under
    /// <c>userSelectedDs</c> is the preferred list the whole story.
    /// </remarks>
    public string HeartbeatDatastoreCandidatePolicySetting { get; init; } = "dasConfig.hBDatastoreCandidatePolicy";

    /// <summary>
    /// The advanced HA option that silences vCenter's own warning about a
    /// non-redundant management network.
    /// </summary>
    public string IgnoreRedundantNetworkWarningSetting { get; init; } =
        "dasConfig.option.das.ignoreRedundantNetWarning";

    /// <summary>
    /// The fewest heartbeat datastores a cluster should have configured.
    /// </summary>
    /// <remarks>
    /// vSphere's own number, not invented here: the vSphere Availability guide
    /// and VMware KB 2004739 both recommend at least two heartbeat datastores,
    /// because with only one, losing that single datastore's connectivity
    /// removes HA's only way to distinguish a network-partitioned host from
    /// one that has actually failed on the storage channel too.
    /// </remarks>
    public int MinimumHeartbeatDatastores { get; init; } = 2;
}
