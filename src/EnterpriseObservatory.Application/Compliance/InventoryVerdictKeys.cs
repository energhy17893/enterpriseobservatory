namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// The <c>Entity.Settings</c> keys the maintenance (M8.4) and expiry (M8.7)
/// checks read.
/// </summary>
/// <remarks>
/// The application layer must not depend on a collector, so these are the
/// vSphere collector's <c>InventoryVerdicts</c> names written a second time;
/// a drift test where both projects are in reach pins them together (the
/// same arrangement as <c>ClusterHighAvailabilityPolicy</c>). An absent key
/// means "not read", never "none".
/// </remarks>
public static class InventoryVerdictKeys
{
    /// <summary>Host or VM: when the configuration tier read the carried keys, ISO-8601 UTC.</summary>
    public const string ConfigurationReadAtUtc = "configuration.readAtUtc";
    /// <summary>VM: CD/DVD drives currently connected, any backing.</summary>
    public const string ConnectedCdroms = "cdrom.connected";

    /// <summary>VM: of those, the ones backed by an ISO file.</summary>
    public const string ConnectedIsoCdroms = "cdrom.connectedIso";

    /// <summary>VM: legacy E1000 network adapters, by count (eo-bestpractice).</summary>
    public const string LegacyAdapterE1000 = "adapter.legacy.e1000";

    /// <summary>VM: legacy E1000e network adapters, by count.</summary>
    public const string LegacyAdapterE1000e = "adapter.legacy.e1000e";

    /// <summary>VM: legacy LSI Logic Parallel SCSI controllers, by count.</summary>
    public const string LegacyAdapterLsiLogic = "adapter.legacy.lsiLogic";

    /// <summary>VM: vCenter's <c>runtime.consolidationNeeded</c>, <c>true</c>/<c>false</c>.</summary>
    public const string ConsolidationNeeded = "consolidationNeeded";

    /// <summary>VM: how many VMware snapshots it has, <c>0</c> when none (eo-simplivity).</summary>
    public const string SnapshotCount = "snapshot.count";

    /// <summary>Cluster: <c>configurationEx.dpmConfigInfo.enabled</c>, <c>true</c>/<c>false</c> (eo-simplivity).</summary>
    public const string ClusterDpmEnabled = "dpmConfigInfo.enabled";

    /// <summary>Cluster: the HA admission control policy's vim25 type name (eo-simplivity).</summary>
    public const string ClusterAdmissionControlPolicyType = "dasConfig.admissionControlPolicy.type";

    /// <summary>Cluster: <c>true</c> when a current EVC mode is set.</summary>
    public const string EvcEnabled = "evc.enabled";

    /// <summary>Cluster: the current EVC mode key, when EVC is on.</summary>
    public const string EvcModeKey = "evc.modeKey";

    /// <summary>Host: <c>summary.maxEVCModeKey</c>, the newest EVC mode its CPU can run.</summary>
    public const string HostMaxEvcModeKey = "evc.maxModeKey";

    /// <summary>Datastore: how many hosts have it mounted.</summary>
    public const string MountedHostCount = "mountedHosts";

    /// <summary>Host or vCenter: the certificate's notAfter, ISO-8601 UTC.</summary>
    public const string CertificateNotAfter = "certificate.notAfter";

    /// <summary>Host or vCenter: the certificate's SHA-256 fingerprint, upper-case hex.</summary>
    public const string CertificateSha256 = "certificate.sha256";

    /// <summary>VM: <c>true</c> when its custom attributes and their definitions were read (M8.8).</summary>
    public const string BackupRead = "backup.read";

    /// <summary>VM: the name of the custom attribute the last backup time was read from.</summary>
    public const string BackupField = "backup.field";

    /// <summary>VM: that attribute's value, as the backup product wrote it.</summary>
    public const string BackupValue = "backup.value";

    /// <summary>VM: the value read as a time, ISO-8601 UTC; absent when it could not be read.</summary>
    public const string BackupLastUtc = "backup.lastUtc";

    /// <summary>VM: which clock the value was read in (it carries no offset of its own).</summary>
    public const string BackupTimeBasis = "backup.timeBasis";

    /// <summary>
    /// VM: the newest PROTECTED SimpliVity backup's <c>created_at</c>, ISO-8601
    /// UTC — an annotation from the SimpliVity source (ADR-0027), absent when
    /// it has none or has not answered for two days.
    /// </summary>
    public const string SimplivityBackupLastUtc = "simplivity.backup.lastUtc";

    /// <summary>The prefix every SimpliVity annotation key carries; its presence scopes eo-simplivity.</summary>
    public const string SimplivityPrefix = "simplivity.";

    // The rest of the SimpliVity annotation (ADR-0027), as the collector
    // writes it and the SimpliVity page reads it. Here rather than in the
    // collector so both use one name and cannot drift. Absent = not read.

    /// <summary>VM: that backup's <c>type</c>.</summary>
    public const string SimplivityBackupType = "simplivity.backup.type";

    /// <summary>Host: <c>state</c> (ALIVE, FAULTY, SUSPECTED…).</summary>
    public const string SimplivityState = "simplivity.state";

    /// <summary>Host and cluster: <c>upgrade_state</c>.</summary>
    public const string SimplivityUpgradeState = "simplivity.upgrade_state";

    /// <summary>Host and cluster: <c>version</c>.</summary>
    public const string SimplivityVersion = "simplivity.version";

    /// <summary>Host: its OVC's name.</summary>
    public const string SimplivityVirtualControllerName = "simplivity.virtual_controller_name";

    /// <summary>Cluster: the OmniStack cluster's own name.</summary>
    public const string SimplivityName = "simplivity.name";

    /// <summary>Cluster: <c>arbiter_required</c>, "true"/"false".</summary>
    public const string SimplivityArbiterRequired = "simplivity.arbiter_required";

    /// <summary>Cluster: <c>arbiter_configured</c>, "true"/"false".</summary>
    public const string SimplivityArbiterConfigured = "simplivity.arbiter_configured";

    /// <summary>Cluster: <c>arbiter_connected</c>, "true"/"false".</summary>
    public const string SimplivityArbiterConnected = "simplivity.arbiter_connected";

    /// <summary>Cluster: how many member hosts it lists.</summary>
    public const string SimplivityMembers = "simplivity.members";

    /// <summary>VM: storage <c>ha_status</c> (SAFE, DEGRADED, DEFUNCT, SYNCING, OUT_OF_SCOPE).</summary>
    public const string SimplivityHaStatus = "simplivity.ha_status";

    /// <summary>VM: <c>ha_resynchronization_progress</c>.</summary>
    public const string SimplivityHaResyncProgress = "simplivity.ha_resynchronization_progress";

    // The Redfish (iLO) annotation on an ESXi host (ADR-0027, M6.1): the keys
    // an alert rests on, so the entity page can show one it lacks as Unknown.

    /// <summary>Host: <c>Systems/1.Oem.Hpe.AggregateHealthStatus.AggregateServerHealth</c>.</summary>
    public const string RedfishAggregateHealth = "redfish.aggregate_health";

    /// <summary>Host: <c>Chassis/1/Power.Redundancy[].Status.Health</c> (OK = redundant).</summary>
    public const string RedfishPsuRedundancy = "redfish.psu.redundancy";

    /// <summary>Host: <c>AggregateHealthStatus.FanRedundancy</c> (iLO 5's Thermal has no Redundancy[]).</summary>
    public const string RedfishFanRedundancy = "redfish.fan.redundancy";

    // Host hardware (S4), from GET /api/hosts/{id}/hardware. A colour
    // (GREEN/YELLOW/RED) or HPE's health word; empty or missing is absent.

    /// <summary>Host: the hardware tree's own <c>host.status</c> colour.</summary>
    public const string SimplivityHwStatus = "simplivity.hw.status";

    /// <summary>Host: <c>raid_card.status</c>.</summary>
    public const string SimplivityHwRaidStatus = "simplivity.hw.raid_status";

    /// <summary>Host: <c>battery.status</c>.</summary>
    public const string SimplivityHwBatteryStatus = "simplivity.hw.battery_status";

    /// <summary>Host: <c>battery.health</c> (HEALTHY…).</summary>
    public const string SimplivityHwBatteryHealth = "simplivity.hw.battery_health";

    /// <summary>Host: <c>battery.percent_charged</c>; absent when HPE answers -1.</summary>
    public const string SimplivityHwBatteryCharge = "simplivity.hw.battery_percent_charged";

    /// <summary>Host: <c>accelerator_card.status</c>; absent on a host without the card.</summary>
    public const string SimplivityHwAcceleratorStatus = "simplivity.hw.accelerator_status";

    /// <summary>Host: physical drives in the tree.</summary>
    public const string SimplivityHwDrives = "simplivity.hw.drives";

    /// <summary>Host: physical drives by <c>status</c>, "GREEN=11;RED=1"; a drive without one is not counted.</summary>
    public const string SimplivityHwDriveStatus = "simplivity.hw.drive_status";

    /// <summary>Host: physical drives by <c>health</c>, "HEALTHY=12".</summary>
    public const string SimplivityHwDriveHealth = "simplivity.hw.drive_health";

    /// <summary>Host: the lowest SSD <c>life_remaining</c>, percent.</summary>
    public const string SimplivityHwLifeRemainingMin = "simplivity.hw.life_remaining_min";

    /// <summary>Host: drives whose <c>percent_rebuilt</c> is 0–99 (HPE answers -1 when not rebuilding).</summary>
    public const string SimplivityHwDrivesRebuilding = "simplivity.hw.drives_rebuilding";
}
