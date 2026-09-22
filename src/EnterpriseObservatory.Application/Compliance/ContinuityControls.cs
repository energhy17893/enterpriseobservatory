using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// The control ids of the product's own continuity catalogue, <c>eo-continuity</c>.
/// </summary>
/// <remarks>
/// One control per distinct finding kind. An id never changes meaning: a
/// control whose meaning changes gets a new id (see <see cref="ContinuityCatalogue"/>).
/// The subject of each is the thing the operator will fix (K1 note §3.1).
/// </remarks>
public static class ContinuityControls
{
    /// <summary>Cluster, subject <c>''</c>: vSphere HA is on.</summary>
    public const string HaEnabled = "eo-cont.ha-enabled";

    /// <summary>Cluster, subject <c>''</c>: HA admission control is on.</summary>
    public const string HaAdmissionControl = "eo-cont.ha-admission-control";

    /// <summary>Cluster, subject <c>''</c>: HA host monitoring is on.</summary>
    public const string HaHostMonitoring = "eo-cont.ha-host-monitoring";

    /// <summary>Cluster, subject <c>''</c>: the APD/PDL response is not disabled.</summary>
    public const string HaStorageProtection = "eo-cont.ha-storage-protection";

    /// <summary>Cluster, subject <c>''</c>: hand-picked heartbeat datastores number at least two.</summary>
    public const string HaHeartbeatDatastores = "eo-cont.ha-heartbeat-datastores";

    /// <summary>Cluster, subject <c>''</c>: vCenter's network redundancy warning is not silenced.</summary>
    public const string HaNetworkWarning = "eo-cont.ha-network-warning";

    /// <summary>Cluster, subject = DRS <c>ruleUuid</c> (the name only when no uuid): the rule holds.</summary>
    public const string DrsRule = "eo-cont.drs-rule";

    /// <summary>Host, subject = device NAA: a shared device has more than one path.</summary>
    public const string PathSingle = "eo-cont.path-single";

    /// <summary>Host, subject = HBA name: shared devices do not depend on this one HBA.</summary>
    public const string PathSingleHba = "eo-cont.path-single-hba";

    /// <summary>Host, subject = target port WWPN: shared devices do not depend on this one array port.</summary>
    public const string PathSingleTarget = "eo-cont.path-single-target";

    /// <summary>Cluster, subject <c>''</c>: the survivors absorb the CPU demand if one host fails.</summary>
    public const string NPlusOneCpu = "eo-cont.n-plus-one-cpu";

    /// <summary>Cluster, subject <c>''</c>: the survivors absorb the memory demand if one host fails.</summary>
    public const string NPlusOneMemory = "eo-cont.n-plus-one-mem";

    private const string AvailabilityGuide = "vSphere Availability guide";

    /// <summary>
    /// Every continuity check production registers, in catalogue order.
    /// </summary>
    /// <remarks>
    /// Each control names the basis of its expectation in
    /// <see cref="ComplianceControl.Source"/>: a Broadcom document or KB when
    /// one says it, "product policy" when the number is ours (reference-approaches §10.4).
    /// </remarks>
    public static IReadOnlyList<ContinuityCheck> All { get; } =
    [
        Check(HaEnabled, "Cluster", "vSphere HA is enabled", AvailabilityGuide,
            new HighAvailabilityCheck(HighAvailabilityCheck.Aspect.Enabled)),
        Check(HaAdmissionControl, "Cluster", "HA admission control is enabled", AvailabilityGuide,
            new HighAvailabilityCheck(HighAvailabilityCheck.Aspect.AdmissionControl)),
        Check(HaHostMonitoring, "Cluster", "HA host monitoring is enabled", AvailabilityGuide,
            new HighAvailabilityCheck(HighAvailabilityCheck.Aspect.HostMonitoring)),
        Check(HaStorageProtection, "Cluster", "HA responds to APD and PDL storage failures",
            AvailabilityGuide + " (VM Component Protection)",
            new HighAvailabilityCheck(HighAvailabilityCheck.Aspect.StorageProtection)),
        Check(HaHeartbeatDatastores, "Cluster", "At least two HA heartbeat datastores", "VMware KB 2004739",
            new HighAvailabilityCheck(HighAvailabilityCheck.Aspect.HeartbeatDatastores)),
        Check(HaNetworkWarning, "Cluster", "HA management network redundancy warning is not silenced",
            AvailabilityGuide + " (das.ignoreRedundantNetWarning)",
            new HighAvailabilityCheck(HighAvailabilityCheck.Aspect.NetworkWarning)),
        Check(DrsRule, "Cluster", "DRS rules are honoured", "The cluster's own DRS rule, as configured in vCenter",
            new DrsRuleCheck()),
        Check(PathSingle, "ESX", "Shared devices have more than one path",
            "vSphere Storage guide, Managing Multiple Paths",
            new MultipathCheck(MultipathCheck.PathCase.SinglePath)),
        Check(PathSingleHba, "ESX", "Shared devices do not depend on one HBA",
            "vSphere Storage guide, Managing Multiple Paths",
            new MultipathCheck(MultipathCheck.PathCase.SingleHba)),
        Check(PathSingleTarget, "ESX", "Shared devices do not depend on one array target port",
            "vSphere Storage guide, Managing Multiple Paths",
            new MultipathCheck(MultipathCheck.PathCase.SingleTarget)),
        Check(NPlusOneCpu, "Cluster", "The cluster absorbs one host failing (CPU)",
            "Product policy: 90% post-failover ceiling, 30-day warning, 7 days of history",
            new NPlusOneCheck(Analysis.ClusterCapacityResource.Cpu)),
        Check(NPlusOneMemory, "Cluster", "The cluster absorbs one host failing (memory)",
            "Product policy: 90% post-failover ceiling, 30-day warning, 7 days of history",
            new NPlusOneCheck(Analysis.ClusterCapacityResource.Memory)),
    ];

    private static ContinuityCheck Check(
        string id, string component, string title, string source, IComplianceCheck check) =>
        new(new ComplianceControl
        {
            ControlId = id,
            Component = component,
            Title = title,
            Parameter = "N/A",
            Source = source,
        }, check);

    /// <summary>A verdict with the given parts; the shorthand every continuity check uses.</summary>
    internal static CheckVerdict Verdict(
        ComplianceVerdict verdict,
        string expected,
        string? observed = null,
        string? reason = null,
        string subject = "",
        string? label = null) => new()
        {
            Subject = subject,
            SubjectLabel = label,
            Verdict = verdict,
            Expected = expected,
            Observed = observed,
            Reason = reason,
        };

    /// <summary>A few items and how many more, for an Observed that must stay readable.</summary>
    internal static string FirstFew(IReadOnlyCollection<string> items, int shown = 3) =>
        items.Count <= shown
            ? string.Join(", ", items)
            : $"{string.Join(", ", items.Take(shown))} (+{items.Count - shown} more)";

    /// <summary>Whether a cluster's <c>configurationEx</c> was read: any <c>dasConfig</c> key at all.</summary>
    internal static bool ConfigurationRead(Entity cluster) =>
        cluster.Settings.Keys.Any(k => k.StartsWith("dasConfig", StringComparison.OrdinalIgnoreCase));
}
