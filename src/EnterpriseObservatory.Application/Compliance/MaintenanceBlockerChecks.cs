using System.Globalization;
using System.Runtime.CompilerServices;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// A virtual machine with a CD/DVD drive connected (M8.4): a host holding it
/// cannot be emptied by vMotion until somebody disconnects it.
/// </summary>
/// <remarks>
/// <para>
/// Entity = the VM, subject <c>''</c> (K1 §3.1): the fix is "disconnect the
/// drive" in the VM's own settings, once per VM however many drives it has.
/// The count of ISO-backed drives travels in <see cref="CheckVerdict.Observed"/>.
/// </para>
/// <para>
/// Every connected drive counts, whatever its backing. A host device or
/// client device cannot follow the VM at all; an ISO can only if the target
/// host sees its datastore, and which datastore the ISO sits on is not read —
/// so an ISO is not given the benefit of the doubt. A powered-off VM reports
/// its drives disconnected and passes.
/// </para>
/// </remarks>
public sealed class ConnectedCdromCheck : IComplianceCheck
{
    private const string Expected = "no CD/DVD drive connected";

    public EntityKind AppliesTo => EntityKind.VirtualMachine;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!TryCount(entity, InventoryVerdictKeys.ConnectedCdroms, out var connected))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, Expected, reason:
                    "The virtual machine's devices (config.hardware.device) were not read, so whether a " +
                    "CD/DVD drive is connected cannot be said."),
            ];
        }

        if (connected == 0)
        {
            return [Verdict(ComplianceVerdict.Passing, Expected, "none connected")];
        }

        var iso = TryCount(entity, InventoryVerdictKeys.ConnectedIsoCdroms, out var n) ? n : (int?)null;

        var observed =
            $"{connected} CD/DVD {(connected == 1 ? "drive" : "drives")} connected" +
            (iso is { } isoCount ? $", {isoCount} backed by an ISO file" : string.Empty) +
            ": vMotion off this host needs it disconnected first";

        return [Verdict(ComplianceVerdict.Failing, Expected, observed)];
    }

    internal static bool TryCount(Entity entity, string key, out int count)
    {
        count = 0;

        return entity.Settings.TryGetValue(key, out var raw) &&
               int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }
}

/// <summary>
/// A virtual machine whose disks need consolidation (M8.4), as vCenter itself
/// reports it in <c>runtime.consolidationNeeded</c>.
/// </summary>
/// <remarks>
/// Entity = the VM, subject <c>''</c>: the fix is "Consolidate" on that VM.
/// Exported, not computed ("export first", reference-approaches §10.5): the
/// verdict is vCenter's, this check only carries it into the finding
/// lifecycle. A value other than <c>true</c>/<c>false</c> is a shape nobody
/// has seen and is not evaluated rather than guessed.
/// </remarks>
public sealed class ConsolidationCheck : IComplianceCheck
{
    private const string Expected = "no disk consolidation needed";

    public EntityKind AppliesTo => EntityKind.VirtualMachine;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!entity.Settings.TryGetValue(InventoryVerdictKeys.ConsolidationNeeded, out var raw))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, Expected, reason:
                    "vCenter's consolidationNeeded flag was not read for this virtual machine."),
            ];
        }

        if (!bool.TryParse(raw, out var needed))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, Expected, raw, reason:
                    $"vCenter reported consolidationNeeded as '{raw}', which is neither true nor false."),
            ];
        }

        return needed
            ? [Verdict(ComplianceVerdict.Failing, Expected,
                "vCenter reports that this virtual machine's disks need consolidation: leftover snapshot " +
                "files sit beside its disks")]
            : [Verdict(ComplianceVerdict.Passing, Expected, "not needed")];
    }
}

/// <summary>
/// A datastore mounted on one host that running virtual machines depend on
/// (M8.4): those machines cannot be moved off that host by vMotion, so the
/// host cannot be emptied for maintenance.
/// </summary>
/// <remarks>
/// <para>
/// Entity = the datastore, subject <c>''</c> (K1 §3.1): the fix is on the
/// datastore — mount it on the other hosts, or Storage vMotion the machines
/// off it — not on each VM and not on the host. The machines and the host
/// they pin go into <see cref="CheckVerdict.Observed"/>.
/// </para>
/// <para>
/// A machine counts when it is backed by the datastore (the graph's
/// <c>BackedBy</c> edge, which also covers an ISO on it) and is running: a
/// powered-off machine is not moved by vMotion, and one whose power state was
/// not collected is kept, because silence about power is not evidence it is
/// off. vSphere Cluster Services agent VMs (<c>vCLS…</c>) are left out:
/// vCenter powers them off or moves them itself when a host enters
/// maintenance, so they pin nothing. A single-host datastore with nothing
/// running on it passes — it pins nothing today.
/// </para>
/// </remarks>
public sealed class SingleHostDatastoreCheck : IComplianceCheck
{
    private const string Expected = "mounted on at least 2 hosts, or no running virtual machine on it";
    private const string PowerStateSetting = "powerState";
    private const string PoweredOnValue = "poweredOn";
    private const string ClusterServicesPrefix = "vCLS";

    private static readonly ConditionalWeakTable<EntityGraph, Dictionary<EntityId, List<EntityId>>> Backing = [];

    public EntityKind AppliesTo => EntityKind.Datastore;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        if (!ConnectedCdromCheck.TryCount(entity, InventoryVerdictKeys.MountedHostCount, out var mounted))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, Expected, reason:
                    "Which hosts mount this datastore (Datastore.host) was not read."),
            ];
        }

        if (mounted >= 2)
        {
            return [Verdict(ComplianceVerdict.Passing, Expected, $"mounted on {mounted} hosts")];
        }

        var graph = context.Graph;
        var running = Backing.GetValue(graph, VmsByDatastore)
            .GetValueOrDefault(entity.Id, [])
            .Select(id => graph.Entities.TryGetValue(id, out var vm) ? vm : null)
            .OfType<Entity>()
            .Where(vm => vm.ObservationState != ObservationState.Vanished)
            .Where(IsRunning)
            .Where(vm => !vm.DisplayName.StartsWith(ClusterServicesPrefix, StringComparison.Ordinal))
            .ToList();

        var mountedOn = mounted == 1 ? "mounted on 1 host" : "mounted on no host";

        if (running.Count == 0)
        {
            return [Verdict(ComplianceVerdict.Passing, Expected, $"{mountedOn}; no running virtual machine on it")];
        }

        var hosts = running
            .Select(vm => HostOf(graph, vm.Id))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var names = running.Select(vm => vm.DisplayName).Order(StringComparer.OrdinalIgnoreCase).ToList();

        var observed =
            $"{mountedOn}; {running.Count} running virtual {(running.Count == 1 ? "machine" : "machines")} " +
            $"pinned{(hosts.Count > 0 ? $" to {string.Join(", ", hosts)}" : string.Empty)}: {FirstFew(names)}";

        return [Verdict(ComplianceVerdict.Failing, Expected, observed)];
    }

    private static bool IsRunning(Entity vm) =>
        !vm.Settings.TryGetValue(PowerStateSetting, out var power) ||
        string.Equals(power, PoweredOnValue, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<EntityId, List<EntityId>> VmsByDatastore(EntityGraph graph)
    {
        var map = new Dictionary<EntityId, List<EntityId>>();

        foreach (var edge in graph.Relationships.Where(r => r.Kind == RelationshipKind.BackedBy))
        {
            if (!graph.Entities.TryGetValue(edge.From, out var from) || from.Kind != EntityKind.VirtualMachine)
            {
                continue;
            }

            if (!map.TryGetValue(edge.To, out var vms))
            {
                map[edge.To] = vms = [];
            }

            vms.Add(edge.From);
        }

        return map;
    }

    private static string? HostOf(EntityGraph graph, EntityId vm)
    {
        var edge = graph.Relationships.FirstOrDefault(r => r.Kind == RelationshipKind.RunsOn && r.From == vm);

        return edge is null
            ? null
            : graph.Entities.TryGetValue(edge.To, out var host) ? host.DisplayName : edge.To.Value;
    }
}

/// <summary>
/// Whether a cluster's hosts can take each other's virtual machines by
/// vMotion whatever their CPU generation (M8.4): EVC.
/// </summary>
/// <remarks>
/// <para>
/// Entity = the cluster, subject <c>''</c>: EVC is one cluster setting.
/// </para>
/// <para>
/// EVC on passes. EVC off is only a blocker when the hosts' CPU generations
/// differ, and that is read from each host's <c>summary.maxEVCModeKey</c>,
/// which is not collected (collection PR 1 measured it and left it out). So
/// EVC off is not evaluated, with that reason — never a failure on a guess,
/// never a pass on silence.
/// </para>
/// </remarks>
public sealed class EvcCheck : IComplianceCheck
{
    private const string Expected = "EVC on, or every host of the cluster the same CPU generation";

    public EntityKind AppliesTo => EntityKind.Cluster;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!entity.Settings.TryGetValue(InventoryVerdictKeys.EvcEnabled, out var raw) ||
            !bool.TryParse(raw, out var enabled))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, Expected, reason:
                    "The cluster summary (EVC mode) was not read."),
            ];
        }

        if (enabled)
        {
            var mode = entity.Settings.GetValueOrDefault(InventoryVerdictKeys.EvcModeKey);

            return [Verdict(ComplianceVerdict.Passing, Expected, mode is null ? "EVC on" : $"EVC on ({mode})")];
        }

        return
        [
            Verdict(ComplianceVerdict.NotEvaluated, Expected, "EVC off", reason:
                "EVC is off, which only blocks vMotion when the hosts' CPU generations differ; each host's " +
                "summary.maxEVCModeKey is not collected, so whether they differ cannot be said."),
        ];
    }
}
