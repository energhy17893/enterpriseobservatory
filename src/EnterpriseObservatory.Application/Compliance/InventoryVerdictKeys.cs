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
    /// <summary>VM: CD/DVD drives currently connected, any backing.</summary>
    public const string ConnectedCdroms = "cdrom.connected";

    /// <summary>VM: of those, the ones backed by an ISO file.</summary>
    public const string ConnectedIsoCdroms = "cdrom.connectedIso";

    /// <summary>VM: vCenter's <c>runtime.consolidationNeeded</c>, <c>true</c>/<c>false</c>.</summary>
    public const string ConsolidationNeeded = "consolidationNeeded";

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
}
