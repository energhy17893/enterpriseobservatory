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

    /// <summary>Cluster, subject <c>''</c>: the HA admission control policy is a cluster resource percentage.</summary>
    /// <remarks>Only the policy type; HA and admission control being on are eo-continuity's findings.</remarks>
    public const string AdmissionControlPolicy = "svt.admission-control-policy";

    /// <summary>Cluster, subject <c>''</c>: no VMware snapshots on its SimpliVity VMs, one finding with a count.</summary>
    public const string VmSnapshots = "svt.vm-snapshots";

    /// <summary>vCenter (standing in for the federation), subject <c>''</c>: at most two OmniStack versions.</summary>
    public const string MixedVersions = "svt.mixed-versions";

    /// <summary>Cluster, subject <c>''</c>: no OmniStack upgrade waiting to be committed.</summary>
    public const string UpgradeCommitNeeded = "svt.upgrade-commit-needed";

    /// <summary>Cluster, subject <c>''</c>: every host of the OmniStack cluster has the same NTP servers.</summary>
    public const string NtpConsistent = "svt.ntp-consistent";

    // S2b: the host-side cross-environment rules.

    /// <summary>ESXi host, subject <c>''</c>: its OVC's memory is fully reserved.</summary>
    public const string OvcReservation = "svt.ovc-reservation";

    /// <summary>ESXi host, subject <c>''</c>: its OVC sits in the cluster's root resource pool.</summary>
    public const string OvcNotInPool = "svt.ovc-not-in-pool";

    /// <summary>ESXi host, subject <c>''</c>: in lockdown mode, the Digital Vault account is an exception user.</summary>
    public const string LockdownException = "svt.lockdown-exception";

    /// <summary>ESXi host, subject <c>''</c>: the SimpliVity storage vmkernel adapter and its switch run MTU 9000.</summary>
    public const string VmkMtu = "svt.vmk-mtu";

    /// <summary>ESXi host, subject <c>''</c>: no more than 100 VMs in DRS "must run on" groups.</summary>
    public const string DrsMustGroup = "svt.drs-must-group";

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
        Check(AdmissionControlPolicy, "Cluster",
            "HA admission control reserves a cluster resource percentage (not slots, not a dedicated failover host)",
            "configurationEx.dasConfig.admissionControlPolicy",
            AdminGuide + " GUID-5EBC5FC4, GUID-C8D90369 ('Cluster resource percentage'; the OVC-reservation " +
            "formula itself is not judged yet). HA and admission control being on: eo-cont.ha-enabled, " +
            "eo-cont.ha-admission-control",
            new SimplivityClusterSettingCheck(SimplivityClusterSettingCheck.Aspect.AdmissionControlPolicy)),
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
        Check(OvcReservation, "ESX", "OVC memory fully reserved — OmniStack host",
            "config.memoryAllocation.reservation, config.hardware.memoryMB (the OVC VM)",
            AdminGuide + " GUID-5AACD6F9, GUID-3BB703E5 (OVC VM settings left as deployed); GUID-5EBC5FC4 " +
            "(the admission control formula counts the OVC's reservation)",
            new SimplivityHostCheck(SimplivityHostCheck.Aspect.OvcReservation)),
        Check(OvcNotInPool, "ESX", "OVC not in a resource pool — OmniStack host",
            "resourcePool (the OVC VM and the cluster's root)",
            AdminGuide + " GUID-5AACD6F9, GUID-3BB703E5 (the OVC VM is not placed in a resource pool)",
            new SimplivityHostCheck(SimplivityHostCheck.Aspect.OvcNotInPool)),
        Check(LockdownException, "ESX", "Digital Vault account is a lockdown exception — OmniStack host",
            "config.lockdownMode, HostAccessManager.QueryLockdownExceptions",
            "HPE OmniStack 5.1.0U1 for vSphere Upgrade Guide (sd00004307): \"If you enabled VMware ESXi lockdown " +
            "mode, ensure that the ESXi administrator or root account that is stored in the HPE SimpliVity Digital " +
            "Vault is added to the Lockdown Exception user list on the HPE OmniStack host.\"; " + AdminGuide +
            " GUID-4B8A8E70; HPE SimpliVity Upgrade Guide (sd00005177) GUID-4AA963F4",
            new SimplivityHostCheck(SimplivityHostCheck.Aspect.LockdownException)),
        Check(VmkMtu, "ESX", "Storage network MTU 9000 — OmniStack host",
            "config.network.vnic[].spec.mtu, config.network.vswitch[].mtu (port groups SVT_StorPG, " +
            "SVT_StoragePortGroup, SVT_FedPortGroup)",
            "HPE SimpliVity networking (a50000783enw; storage/federation MTU 9000 end to end); " + AdminGuide +
            " GUID-E3460A32",
            new SimplivityHostCheck(SimplivityHostCheck.Aspect.VmkernelMtu)),
        Check(DrsMustGroup, "ESX", "At most 100 VMs in DRS must-run groups — OmniStack host",
            "configurationEx.rule, configurationEx.group",
            AdminGuide + " GUID-87E50895, GUID-FA0E3F6C (DRS \"must run on\" group: no more than 100 VMs per host)",
            new SimplivityHostCheck(SimplivityHostCheck.Aspect.DrsMustGroup)),
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
