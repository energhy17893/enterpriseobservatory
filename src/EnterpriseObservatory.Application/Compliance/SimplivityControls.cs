using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// The control ids of the product's own SimpliVity catalogue, <c>eo-simplivity</c>.
/// </summary>
/// <remarks>
/// Same rules as <see cref="ContinuityControls"/>: one control per distinct
/// finding kind, an id never changes meaning. Each is a state that does not
/// clear by itself, so a finding rather than an alarm (ADR-0024); the
/// SimpliVity events that announce some of them stay alarms beside these.
/// </remarks>
public static class SimplivityControls
{
    /// <summary>Cluster, subject <c>''</c>: DPM is off on an OmniStack cluster.</summary>
    public const string DpmOff = "svt.dpm-off";

    /// <summary>Cluster, subject <c>''</c>: vSphere HA is on in an OmniStack cluster.</summary>
    public const string ClusterHaOn = "svt.cluster-ha-on";

    /// <summary>Cluster, subject <c>''</c>: HA admission control is on and not the slot (failover level) policy.</summary>
    public const string AdmissionControlConfigured = "svt.admission-control-configured";

    /// <summary>Cluster, subject <c>''</c>: no VMware snapshots on its SimpliVity VMs, one finding with a count.</summary>
    public const string VmSnapshots = "svt.vm-snapshots";

    /// <summary>vCenter (standing in for the federation), subject <c>''</c>: at most two OmniStack versions.</summary>
    public const string MixedVersions = "svt.mixed-versions";

    /// <summary>Cluster, subject <c>''</c>: no OmniStack upgrade waiting to be committed.</summary>
    public const string UpgradeCommitNeeded = "svt.upgrade-commit-needed";

    /// <summary>Cluster, subject <c>''</c>: every host of the OmniStack cluster has the same NTP servers.</summary>
    public const string NtpConsistent = "svt.ntp-consistent";

    private const string AdminGuide = "HPE SimpliVity Administration Guide 5.2.0 (sd00005173)";

    /// <summary>
    /// Every SimpliVity check production registers, in catalogue order.
    /// </summary>
    public static IReadOnlyList<SimplivityCheck> All { get; } =
    [
        Check(DpmOff, "Cluster", "DPM is off on the OmniStack cluster",
            "configurationEx.dpmConfigInfo.enabled",
            AdminGuide + " GUID-CB42AB7B (alarm 'DPM enabled on OmniCube system')",
            new SimplivityClusterSettingCheck(SimplivityClusterSettingCheck.Aspect.DpmOff)),
        Check(ClusterHaOn, "Cluster", "vSphere HA is on in the OmniStack cluster",
            "configurationEx.dasConfig.enabled",
            "HPE SimpliVity Events Reference 5.2.0 (sd00005179) GUID-5EBBA0A1 ('MVA cluster has vSphere HA disabled')",
            new SimplivityClusterSettingCheck(SimplivityClusterSettingCheck.Aspect.HaOn)),
        Check(AdmissionControlConfigured, "Cluster",
            "HA admission control is on and reserves a percentage, not slots (failover level)",
            "configurationEx.dasConfig.admissionControlEnabled, configurationEx.dasConfig.admissionControlPolicy",
            AdminGuide + " GUID-5EBC5FC4, GUID-C8D90369 ('Cluster resource percentage'; the OVC-reservation " +
            "formula itself is not judged yet)",
            new SimplivityClusterSettingCheck(SimplivityClusterSettingCheck.Aspect.AdmissionControl)),
        Check(VmSnapshots, "Cluster",
            "No VMware snapshots on SimpliVity VMs (a conflict with SimpliVity backups, judged whatever " +
            "their age; not the 'Snapshot left behind' age alarm)",
            "snapshot",
            AdminGuide + " GUID-FF3CDFC4 (VMware snapshots not recommended in production; " +
            "alarm 'VM Backup Snapshot Failure')",
            new SimplivitySnapshotCheck()),
        Check(MixedVersions, "vCenter",
            "No more than two OmniStack versions in the federation (judged per vCenter)",
            "simplivity.version (hosts)",
            AdminGuide + " GUID-511CF9D3 ('Mixed version, upgrade needed')",
            new SimplivityMixedVersionCheck()),
        Check(UpgradeCommitNeeded, "Cluster", "No OmniStack upgrade is waiting to be committed",
            "simplivity.upgrade_state (cluster)",
            "HPE SimpliVity Command Reference 5.1.0U1 (sd00004299) GUID-E07AB4E4, svt-software-status-show " +
            "('Ready to commit', 'Mixed version'); the commit-needed event stays an alarm",
            new SimplivityClusterSettingCheck(SimplivityClusterSettingCheck.Aspect.UpgradeCommit)),
        Check(NtpConsistent, "Cluster", "Every host of the OmniStack cluster uses the same NTP servers",
            "config.dateTimeInfo.ntpConfig.server",
            AdminGuide + " GUID-E3460A32 (NTP the same on OVC, ESXi and vCenter; only the ESXi hosts are " +
            "compared, the OVC's and vCenter's own NTP are not collected)",
            new SimplivityNtpCheck()),
    ];

    private static SimplivityCheck Check(
        string id, string component, string title, string parameter, string source, IComplianceCheck check) =>
        new(new ComplianceControl
        {
            ControlId = id,
            Component = component,
            Title = title,
            Parameter = parameter,
            Source = source,
        }, check);
}
