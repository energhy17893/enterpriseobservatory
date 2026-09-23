using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// A virtual machine whose memory limit is below its configured memory
/// (vSphere 8.0 U3 Performance Best Practices guide): the guest is throttled
/// below what it was sized for, which looks like contention from inside it.
/// </summary>
/// <remarks>
/// <para>
/// Entity = the VM, subject <c>''</c>: the fix is the VM's own memory limit.
/// Binary fact only, no invented threshold (reference-approaches §10.4): the
/// limit is either below the configured memory or it is not.
/// </para>
/// <para>
/// vCenter's own "no limit" is <c>-1</c> (<see cref="EntitySizing.IsMemoryLimited"/>),
/// kept apart from a real ceiling and from "not read": no limit passes, a real
/// limit below configured memory fails, and either figure missing is not
/// evaluated.
/// </para>
/// </remarks>
public sealed class MemoryLimitCheck : IComplianceCheck
{
    private const string Expected = "no memory limit below the configured memory";

    public EntityKind AppliesTo => EntityKind.VirtualMachine;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var sizing = entity.Sizing;

        if (sizing?.ConfiguredMemoryMb is not { } configured || sizing.MemoryLimitMb is not { } limit)
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, Expected, reason:
                    "The virtual machine's configured memory or memory limit " +
                    "(config.hardware.memoryMB / config.memoryAllocation.limit) was not read."),
            ];
        }

        if (!sizing.IsMemoryLimited)
        {
            return [Verdict(ComplianceVerdict.Passing, Expected, "no limit (unlimited)")];
        }

        return limit < configured
            ? [Verdict(ComplianceVerdict.Failing, Expected,
                $"limit {limit} MB is below the configured memory of {configured} MB")]
            : [Verdict(ComplianceVerdict.Passing, Expected,
                $"limit {limit} MB is at or above the configured memory of {configured} MB")];
    }
}

/// <summary>
/// Legacy virtual network and storage adapters across the estate (vSphere
/// 8.0 U3 Performance Best Practices guide; VMware KB 438023 rightsizing):
/// E1000/E1000e network cards and LSI Logic Parallel SCSI controllers cost
/// more CPU per packet or I/O than the current default (VMXNET3 / PVSCSI).
/// </summary>
/// <remarks>
/// <para>
/// Entity = vCenter, subject = the adapter type (<c>E1000</c>, <c>E1000e</c>,
/// <c>LSI Logic Parallel</c>): one finding per type across the whole estate, carrying
/// how many adapters and, in <see cref="CheckVerdict.Observed"/>, a few of the
/// virtual machines that carry one -- never one finding per virtual machine
/// (reference-approaches, product principle 4).
/// </para>
/// <para>
/// A vanished virtual machine is left out. A type is not evaluated only when
/// no live virtual machine's device list was read at all; once at least one
/// was, the others are simply not counted, the same as the CD/DVD check.
/// </para>
/// </remarks>
public sealed class LegacyVirtualAdapterCheck : IComplianceCheck
{
    private sealed record AdapterType(string SettingKey, string Subject, string Description);

    private static readonly AdapterType[] Types =
    [
        new(InventoryVerdictKeys.LegacyAdapterE1000, "E1000", "legacy E1000 network adapter"),
        new(InventoryVerdictKeys.LegacyAdapterE1000e, "E1000e", "legacy E1000e network adapter"),
        new(InventoryVerdictKeys.LegacyAdapterLsiLogic, "LSI Logic Parallel",
            "legacy LSI Logic Parallel SCSI controller (not LSI Logic SAS, which is still supported)"),
    ];

    public EntityKind AppliesTo => EntityKind.VCenter;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var vms = context.Graph.Entities.Values
            .Where(e => e.Kind == EntityKind.VirtualMachine && e.ObservationState != ObservationState.Vanished)
            .ToList();

        return [.. Types.Select(t => JudgeType(t, vms))];
    }

    private static CheckVerdict JudgeType(AdapterType type, IReadOnlyList<Entity> vms)
    {
        var expected = $"no {type.Description}";

        var read = vms.Where(v => v.Settings.ContainsKey(type.SettingKey)).ToList();

        if (read.Count == 0)
        {
            return Verdict(ComplianceVerdict.NotEvaluated, expected, subject: type.Subject, reason:
                "No live virtual machine's devices (config.hardware.device) were read, so whether " +
                $"a {type.Description} is present cannot be said.");
        }

        var withAdapter = read
            .Select(v => (Vm: v, Count: int.TryParse(v.Settings[type.SettingKey], out var n) ? n : 0))
            .Where(x => x.Count > 0)
            .ToList();

        var total = withAdapter.Sum(x => x.Count);

        if (total == 0)
        {
            return Verdict(ComplianceVerdict.Passing, expected, "none found", subject: type.Subject);
        }

        var names = withAdapter
            .Select(x => x.Vm.DisplayName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var observed =
            $"{total} {(total == 1 ? "adapter" : "adapters")} across {withAdapter.Count} virtual " +
            $"{(withAdapter.Count == 1 ? "machine" : "machines")}: {FirstFew(names)}";

        return Verdict(ComplianceVerdict.Failing, expected, observed, subject: type.Subject);
    }
}
