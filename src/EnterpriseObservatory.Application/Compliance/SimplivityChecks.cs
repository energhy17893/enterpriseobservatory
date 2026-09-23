using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>What every eo-simplivity check shares: its scope and the cluster's hosts.</summary>
internal static class SimplivityScope
{
    /// <summary>
    /// Whether the SimpliVity source folded onto this entity (ADR-0027). An
    /// entity without it is out of scope: no finding, not NotEvaluated.
    /// </summary>
    public static bool IsAnnotated(Entity entity) =>
        entity.Settings.Keys.Any(k => k.StartsWith(InventoryVerdictKeys.SimplivityPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The live hosts that are part of the cluster.</summary>
    public static List<Entity> HostsOf(EntityGraph graph, EntityId cluster) =>
    [
        .. graph.Relationships
            .Where(r => r.Kind == RelationshipKind.PartOf && r.To == cluster)
            .Select(r => graph.Entities.GetValueOrDefault(r.From))
            .OfType<Entity>()
            .Where(h => h.Kind == EntityKind.EsxiHost && h.ObservationState != ObservationState.Vanished)
            .DistinctBy(h => h.Id),
    ];

    public static List<string> Names(IEnumerable<Entity> entities) =>
        [.. entities.Select(e => e.DisplayName).Order(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// <summary>
/// One cluster setting of an OmniStack cluster, judged against HPE's rule
/// (reference-approaches §10.8): DPM off, a percentage admission control
/// policy, no upgrade waiting to be committed.
/// </summary>
/// <remarks>
/// Only clusters that carry a <c>simplivity.*</c> annotation. A setting that
/// was not read is NotEvaluated, never passing. Whether HA and admission
/// control are on at all is eo-continuity's finding (eo-cont.ha-enabled,
/// eo-cont.ha-admission-control), not repeated here (product principle 4):
/// this catalogue judges only what HPE adds on top.
/// </remarks>
public sealed class SimplivityClusterSettingCheck(SimplivityClusterSettingCheck.Aspect aspect) : IComplianceCheck
{
    public enum Aspect
    {
        DpmOff,
        AdmissionControlPolicy,
        UpgradeCommit,
    }

    /// <summary>The "Cluster resource percentage" policy HPE's formula is written for.</summary>
    /// <remarks>
    /// vim25's own type name, plural "Resources" — the first version of this
    /// check spelled it singular, matched nothing vSphere sends, and failed
    /// the four Kibar clusters that use exactly this policy (23 September 2026).
    /// </remarks>
    public const string ResourcePercentagePolicy = "ClusterFailoverResourcesAdmissionControlPolicy";

    private static readonly ClusterHighAvailabilityPolicy Ha = ClusterHighAvailabilityPolicy.Default;

    public Aspect Judges { get; } = aspect;

    public EntityKind AppliesTo => EntityKind.Cluster;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!SimplivityScope.IsAnnotated(entity))
        {
            return [];
        }

        var settings = entity.Settings;

        return
        [
            Judges switch
            {
                Aspect.DpmOff => Dpm(settings),
                Aspect.AdmissionControlPolicy => AdmissionControlPolicy(settings),
                Aspect.UpgradeCommit => UpgradeCommit(settings),
                _ => throw new InvalidOperationException($"Unknown aspect {Judges}."),
            },
        ];
    }

    private static CheckVerdict Dpm(IReadOnlyDictionary<string, string> settings)
    {
        const string expected = "DPM off";

        if (!bool.TryParse(settings.GetValueOrDefault(InventoryVerdictKeys.ClusterDpmEnabled), out var on))
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                "The cluster's DPM setting (configurationEx.dpmConfigInfo.enabled) was not read.");
        }

        return on
            ? Verdict(ComplianceVerdict.Failing, expected,
                "DPM on: vCenter may power off a host whose OmniStack storage the cluster depends on")
            : Verdict(ComplianceVerdict.Passing, expected, "DPM off");
    }

    /// <remarks>
    /// Only the policy type. HA or admission control off, or not read, is not
    /// evaluated and points at the continuity control that owns that finding.
    /// </remarks>
    private static CheckVerdict AdmissionControlPolicy(IReadOnlyDictionary<string, string> settings)
    {
        const string expected = "admission control policy: cluster resource percentage";

        if (!bool.TryParse(settings.GetValueOrDefault(Ha.EnabledSetting), out var ha) || !ha)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                $"vSphere HA is not confirmed on for this cluster, so its admission control policy does not apply; see {ContinuityControls.HaEnabled}.");
        }

        if (!bool.TryParse(settings.GetValueOrDefault(Ha.AdmissionControlEnabledSetting), out var on) || !on)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                $"Admission control is not confirmed on for this cluster, so its policy does not apply; see {ContinuityControls.HaAdmissionControl}.");
        }

        if (settings.GetValueOrDefault(InventoryVerdictKeys.ClusterAdmissionControlPolicyType) is not { Length: > 0 } policy)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                "The admission control policy (configurationEx.dasConfig.admissionControlPolicy) was not read.");
        }

        return policy switch
        {
            ResourcePercentagePolicy => Verdict(ComplianceVerdict.Passing, expected, "policy: cluster resource percentage"),
            "ClusterFailoverHostAdmissionControlPolicy" => Verdict(ComplianceVerdict.Failing, expected, "policy: dedicated failover host"),
            "ClusterFailoverLevelAdmissionControlPolicy" => Verdict(ComplianceVerdict.Failing, expected, "policy: slots (host failures to tolerate)"),
            _ => Verdict(ComplianceVerdict.Failing, expected, $"policy: {policy}"),
        };
    }

    private static CheckVerdict UpgradeCommit(IReadOnlyDictionary<string, string> settings)
    {
        const string expected = "no OmniStack upgrade waiting to be committed";

        if (settings.GetValueOrDefault(InventoryVerdictKeys.SimplivityUpgradeState) is not { Length: > 0 } state)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                "The OmniStack cluster's upgrade_state was not read.");
        }

        return state is "SUCCESS_COMMIT_NEEDED" or "MIXED_VERSION"
            ? Verdict(ComplianceVerdict.Failing, expected, $"{state}: the upgrade is not finished until it is committed")
            : Verdict(ComplianceVerdict.Passing, expected, state);
    }
}

/// <summary>
/// VMware snapshots on the SimpliVity VMs of a cluster (HPE Administration
/// Guide GUID-FF3CDFC4): not recommended in production, and a VM with
/// several fails its SimpliVity backup.
/// </summary>
/// <remarks>
/// <para>
/// Entity = the cluster, subject <c>''</c>: one finding with a count, never
/// one per VM (product principle 4). A cluster with no SimpliVity VM is out
/// of scope.
/// </para>
/// <para>
/// Not the "Snapshot left behind" alarm: that one fires on any VM once a
/// snapshot is days old and clears when it goes. This is the backup conflict,
/// judged whatever the snapshot's age, on SimpliVity VMs only. A VM whose
/// snapshot tree was not read is left out of the count; the cluster is not
/// evaluated only when none was read.
/// </para>
/// </remarks>
public sealed class SimplivitySnapshotCheck : IComplianceCheck
{
    private const string Expected = "no VMware snapshots on SimpliVity VMs";

    public EntityKind AppliesTo => EntityKind.Cluster;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        var graph = context.Graph;
        var hosts = SimplivityScope.HostsOf(graph, entity.Id).Select(h => h.Id).ToHashSet();

        var vms = graph.Relationships
            .Where(r => r.Kind == RelationshipKind.RunsOn && hosts.Contains(r.To))
            .Select(r => graph.Entities.GetValueOrDefault(r.From))
            .OfType<Entity>()
            .Where(v => v.Kind == EntityKind.VirtualMachine && v.ObservationState != ObservationState.Vanished)
            .Where(SimplivityScope.IsAnnotated)
            .DistinctBy(v => v.Id)
            .ToList();

        if (vms.Count == 0)
        {
            return [];
        }

        var read = vms
            .Select(v => (Vm: v, Count: int.TryParse(v.Settings.GetValueOrDefault(InventoryVerdictKeys.SnapshotCount), out var n) ? n : (int?)null))
            .Where(x => x.Count is not null)
            .ToList();

        if (read.Count == 0)
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, Expected, reason:
                    $"The snapshot tree (snapshot) was not read on any of the cluster's {vms.Count} SimpliVity VMs."),
            ];
        }

        var with = read.Where(x => x.Count > 0).ToList();

        if (with.Count == 0)
        {
            return [Verdict(ComplianceVerdict.Passing, Expected, $"none on {read.Count} SimpliVity VMs")];
        }

        var total = with.Sum(x => x.Count!.Value);

        return
        [
            Verdict(ComplianceVerdict.Failing, Expected,
                $"{with.Count} {(with.Count == 1 ? "VM" : "VMs")} with {total} {(total == 1 ? "snapshot" : "snapshots")}: " +
                FirstFew(SimplivityScope.Names(with.Select(x => x.Vm)))),
        ];
    }
}

/// <summary>
/// More than two OmniStack versions among the SimpliVity hosts (HPE
/// Administration Guide GUID-511CF9D3).
/// </summary>
/// <remarks>
/// Entity = the vCenter, subject <c>''</c>: the product has no federation
/// entity, and the SimpliVity annotation does not say which federation a host
/// belongs to. OmniStack's /api/version returns <c>federation_id</c>; once the
/// SimpliVity source (S3) adds it to the annotation this moves to the
/// federation. ponytail: until then one vCenter stands in for one federation; a
/// federation across vCenters is judged per vCenter and two federations
/// under one vCenter are judged together — key on a federation id once the
/// collector records one.
/// </remarks>
public sealed class SimplivityMixedVersionCheck : IComplianceCheck
{
    private const string Expected = "no more than two OmniStack versions in the federation";

    private const int MaximumVersions = 2;

    public EntityKind AppliesTo => EntityKind.VCenter;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        var hosts = context.Graph.Entities.Values
            .Where(h => h.Kind == EntityKind.EsxiHost && h.ObservationState != ObservationState.Vanished)
            .Where(h => string.Equals(h.SourceInstanceId, entity.SourceInstanceId, StringComparison.Ordinal))
            .Where(SimplivityScope.IsAnnotated)
            .ToList();

        if (hosts.Count == 0)
        {
            return [];
        }

        var versions = hosts
            .Where(h => h.Settings.GetValueOrDefault(InventoryVerdictKeys.SimplivityVersion) is { Length: > 0 })
            .GroupBy(h => h.Settings[InventoryVerdictKeys.SimplivityVersion], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var observed = string.Join("; ", versions.Select(g => $"{g.Key}: {g.Count()} {(g.Count() == 1 ? "host" : "hosts")}"));

        if (versions.Count > MaximumVersions)
        {
            return [Verdict(ComplianceVerdict.Failing, Expected, $"{versions.Count} versions ({observed})")];
        }

        var unread = hosts.Where(h => !versions.Any(g => g.Contains(h))).ToList();

        if (unread.Count > 0)
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, Expected, observed.Length == 0 ? null : observed, reason:
                    $"The OmniStack version was not read on {FirstFew(SimplivityScope.Names(unread))}."),
            ];
        }

        return [Verdict(ComplianceVerdict.Passing, Expected, $"{versions.Count} {(versions.Count == 1 ? "version" : "versions")} ({observed})")];
    }
}

/// <summary>
/// The same NTP servers on every host of an OmniStack cluster (HPE
/// Administration Guide GUID-E3460A32: NTP the same on OVC, ESXi and vCenter).
/// </summary>
/// <remarks>
/// Entity = the cluster, subject <c>''</c>. Only the ESXi side can be
/// compared: the OVC's and vCenter's own NTP are not collected. The server
/// lists are compared as sets, case-insensitively; order is not a difference.
/// </remarks>
public sealed class SimplivityNtpCheck : IComplianceCheck
{
    private const string Expected = "the same NTP servers on every host of the cluster";

    public EntityKind AppliesTo => EntityKind.Cluster;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        if (!SimplivityScope.IsAnnotated(entity))
        {
            return [];
        }

        var hosts = SimplivityScope.HostsOf(context.Graph, entity.Id);

        if (hosts.Count == 0)
        {
            return [Verdict(ComplianceVerdict.NotEvaluated, Expected, reason: "No host of this cluster was seen.")];
        }

        var configurations = hosts
            .Where(h => h.TimeConfiguration is not null)
            .GroupBy(h => ServerSet(h.TimeConfiguration!), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        if (configurations.Count > 1)
        {
            var observed = string.Join("; ", configurations.Select(g =>
                $"{g.Key}: {FirstFew(SimplivityScope.Names(g))}"));

            return [Verdict(ComplianceVerdict.Failing, Expected, $"{configurations.Count} different sets ({observed})")];
        }

        var unread = hosts.Where(h => h.TimeConfiguration is null).ToList();

        if (unread.Count > 0)
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, Expected, reason:
                    $"The time configuration (config.dateTimeInfo) was not read on {FirstFew(SimplivityScope.Names(unread))}."),
            ];
        }

        return [Verdict(ComplianceVerdict.Passing, Expected, $"{hosts.Count} hosts: {configurations[0].Key}")];
    }

    private static string ServerSet(TimeConfiguration time) =>
        time.NtpServers.Count == 0
            ? "no NTP servers"
            : string.Join(", ", time.NtpServers
                .Select(s => s.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
}
