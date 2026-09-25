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

/// <summary>
/// One of HPE's vSphere-side rules for an OmniStack host and its OVC
/// (reference-approaches §10.8, cross-environment table; S2b).
/// </summary>
/// <remarks>
/// <para>
/// Entity = the host, subject <c>''</c>: the OVC, its vmkernel adapter and
/// its lockdown list are each one per host. Only hosts that carry a
/// <c>simplivity.*</c> annotation.
/// </para>
/// <para>
/// The OVC is the VM that runs on this host (a RunsOn edge) and whose name
/// is the host's <c>simplivity.virtual_controller_name</c>, never a VM found
/// by name pattern alone.
/// </para>
/// </remarks>
public sealed class SimplivityHostCheck(SimplivityHostCheck.Aspect aspect) : IComplianceCheck
{
    public enum Aspect
    {
        OvcReservation,
        OvcNotInPool,
        LockdownException,
        VmkernelMtu,
        DrsMustGroup,
    }

    /// <summary>
    /// HPE's port groups on an OmniStack host's storage switch, as seen live
    /// (docs/measurements/s2b-cross-env-shapes.md, 26 of 26 hosts): the
    /// storage vmkernel adapter sits on <c>SVT_StorPG</c>, the OVC's storage
    /// and federation NICs on the other two. Federation has no vmkernel adapter.
    /// </summary>
    public static readonly IReadOnlySet<string> SimplivityPortGroups =
        new HashSet<string>(["SVT_StorPG", "SVT_StoragePortGroup", "SVT_FedPortGroup"], StringComparer.OrdinalIgnoreCase);

    public const int JumboMtu = 9000;

    /// <summary>HPE: a DRS "must run on" group should not hold more than this many VMs per host.</summary>
    public const int MustGroupVmsPerHost = 100;

    public Aspect Judges { get; } = aspect;

    public EntityKind AppliesTo => EntityKind.EsxiHost;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        if (!SimplivityScope.IsAnnotated(entity))
        {
            return [];
        }

        return
        [
            Judges switch
            {
                Aspect.OvcReservation => OvcReservation(entity, context.Graph),
                Aspect.OvcNotInPool => OvcNotInPool(entity, context.Graph),
                Aspect.LockdownException => LockdownException(entity),
                Aspect.VmkernelMtu => VmkernelMtu(entity),
                Aspect.DrsMustGroup => DrsMustGroup(entity, context.Graph),
                _ => throw new InvalidOperationException($"Unknown aspect {Judges}."),
            },
        ];
    }

    /// <summary>The host's OVC, or why it was not found.</summary>
    private static (Entity? Ovc, string? Reason) Ovc(Entity host, EntityGraph graph)
    {
        if (host.Settings.GetValueOrDefault(InventoryVerdictKeys.SimplivityVirtualControllerName) is not { Length: > 0 } name)
        {
            return (null, "The host's OVC name (simplivity.virtual_controller_name) was not read.");
        }

        var ovc = graph.Relationships
            .Where(r => r.Kind == RelationshipKind.RunsOn && r.To == host.Id)
            .Select(r => graph.Entities.GetValueOrDefault(r.From))
            .OfType<Entity>()
            .FirstOrDefault(v => v.Kind == EntityKind.VirtualMachine &&
                                 v.ObservationState != ObservationState.Vanished &&
                                 string.Equals(v.DisplayName, name, StringComparison.Ordinal));

        return ovc is null ? (null, $"No VM named {name} runs on this host.") : (ovc, null);
    }

    private static Entity? ClusterOf(Entity host, EntityGraph graph) =>
        graph.Relationships
            .Where(r => r.Kind == RelationshipKind.PartOf && r.From == host.Id)
            .Select(r => graph.Entities.GetValueOrDefault(r.To))
            .OfType<Entity>()
            .FirstOrDefault(c => c.Kind == EntityKind.Cluster);

    private static CheckVerdict OvcReservation(Entity host, EntityGraph graph)
    {
        const string expected = "the OVC's memory fully reserved";

        var (ovc, missing) = Ovc(host, graph);
        if (ovc is null)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason: missing);
        }

        if (!long.TryParse(ovc.Settings.GetValueOrDefault(InventoryVerdictKeys.MemoryReservationMb), out var reserved) ||
            ovc.Sizing?.ConfiguredMemoryMb is not { } configured)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                $"{ovc.DisplayName}'s memory reservation (config.memoryAllocation.reservation) or size (config.hardware.memoryMB) was not read.");
        }

        return reserved == configured
            ? Verdict(ComplianceVerdict.Passing, expected, $"{ovc.DisplayName}: {reserved} MB of {configured} MB reserved")
            : Verdict(ComplianceVerdict.Failing, expected,
                $"{ovc.DisplayName}: {reserved} MB of {configured} MB reserved; HPE's admission control formula assumes all of it");
    }

    private static CheckVerdict OvcNotInPool(Entity host, EntityGraph graph)
    {
        const string expected = "the OVC in the cluster's root resource pool, not a child pool";

        var (ovc, missing) = Ovc(host, graph);
        if (ovc is null)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason: missing);
        }

        if (ovc.Settings.GetValueOrDefault(InventoryVerdictKeys.ResourcePool) is not { Length: > 0 } pool)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                $"{ovc.DisplayName}'s resource pool (resourcePool) was not read.");
        }

        if (ClusterOf(host, graph)?.Settings.GetValueOrDefault(InventoryVerdictKeys.ResourcePool) is not { Length: > 0 } root)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                "The cluster's root resource pool (ClusterComputeResource.resourcePool) was not read.");
        }

        return string.Equals(pool, root, StringComparison.Ordinal)
            ? Verdict(ComplianceVerdict.Passing, expected, $"{ovc.DisplayName}: in the cluster's root pool")
            : Verdict(ComplianceVerdict.Failing, expected, $"{ovc.DisplayName}: in resource pool {pool}, not the cluster's root ({root})");
    }

    /// <remarks>
    /// The Digital Vault holds an ESXi administrator or root account of the
    /// customer's choosing (sd00004307), and this product cannot read the
    /// Vault, so a non-empty exception list is not judged: only an empty one
    /// is known to lack it. Lockdown off needs no exception and passes.
    /// </remarks>
    private static CheckVerdict LockdownException(Entity host)
    {
        const string expected = "lockdown mode off, or the Digital Vault account among its exception users";

        if (host.LockdownMode is not { Length: > 0 } mode)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason: "The host's lockdown mode (config.lockdownMode) was not read.");
        }

        if (string.Equals(mode, "lockdownDisabled", StringComparison.Ordinal))
        {
            return Verdict(ComplianceVerdict.Passing, expected, "lockdown mode off");
        }

        if (!host.Settings.TryGetValue(InventoryVerdictKeys.LockdownExceptions, out var raw))
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, mode, reason:
                "The lockdown exception users (HostAccessManager.QueryLockdownExceptions) were not read.");
        }

        var users = Lines(raw);

        return users.Length == 0
            ? Verdict(ComplianceVerdict.Failing, expected, "no exception user at all; the Digital Vault account cannot log in")
            : Verdict(ComplianceVerdict.NotEvaluated, expected, $"{mode}, exception users: {FirstFew(users)}", reason:
                $"this product cannot read which ESXi account the Digital Vault holds; check that it is in this list: {string.Join(", ", users)}");
    }

    /// <remarks>
    /// The storage vmkernel adapter's MTU and the MTU of every standard switch
    /// carrying one of HPE's port groups. A vmkernel adapter on a distributed
    /// port carries no port group name, so a host whose storage adapter is on
    /// one is not evaluated rather than passed.
    /// </remarks>
    private static CheckVerdict VmkernelMtu(Entity host)
    {
        const string expected = "MTU 9000 on the SimpliVity storage vmkernel adapter and its switch";

        if (!host.Settings.TryGetValue(InventoryVerdictKeys.VmkernelMtu, out var vmk) ||
            !host.Settings.TryGetValue(InventoryVerdictKeys.PortGroupSwitchMtu, out var switches))
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                "The vmkernel adapters (config.network.vnic) or standard switches (config.network.vswitch) were not read.");
        }

        var adapters = Svt(vmk).Select(p => (What: $"vmkernel on {p.PortGroup}", p.Mtu)).ToList();

        if (adapters.Count == 0)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                "No vmkernel adapter on a standard port group named SVT_StorPG, HPE's storage port group.");
        }

        var all = adapters.Concat(Svt(switches).Select(p => (What: $"switch of {p.PortGroup}", p.Mtu))).ToList();
        var low = all.Where(a => a.Mtu != JumboMtu).Select(a => $"{a.What} {a.Mtu}").ToList();

        return low.Count > 0
            ? Verdict(ComplianceVerdict.Failing, expected, $"MTU below 9000: {FirstFew(low)}")
            : Verdict(ComplianceVerdict.Passing, expected, $"MTU 9000 on {all.Count} adapters and port groups");

        static IEnumerable<(string PortGroup, int? Mtu)> Svt(string lines) =>
            Lines(lines)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2 && SimplivityPortGroups.Contains(p[0]))
                .Select(p => (p[0], int.TryParse(p[1], out var m) ? m : (int?)null));
    }

    /// <remarks>
    /// Counts every VM an enabled mandatory VM-host rule binds to a host
    /// group this host is in. ponytail: a VM bound by one rule to a group of
    /// several hosts counts on each of them, the worst case; divide by the
    /// group's size if HPE's number turns out to mean the average.
    /// </remarks>
    private static CheckVerdict DrsMustGroup(Entity host, EntityGraph graph)
    {
        var expected = $"no more than {MustGroupVmsPerHost} VMs in DRS \"must run on\" groups per host";

        if (ClusterOf(host, graph) is not { } cluster || !ConfigurationRead(cluster))
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, reason:
                "The cluster's DRS rules and groups (configurationEx) were not read.");
        }

        var vms = cluster.DrsRules
            .Where(r => r is { Enabled: true, Mandatory: true, Kind: DrsRuleKind.VmHostAffine } &&
                        r.HostEntityIds.Contains(host.Id.Value, StringComparer.Ordinal))
            .SelectMany(r => r.VirtualMachineEntityIds)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return vms > MustGroupVmsPerHost
            ? Verdict(ComplianceVerdict.Failing, expected, $"{vms} VMs in must-run groups on this host")
            : Verdict(ComplianceVerdict.Passing, expected, $"{vms} {(vms == 1 ? "VM" : "VMs")} in must-run groups on this host");
    }

    private static string[] Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
