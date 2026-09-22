using System.Runtime.CompilerServices;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// Whether a host's shared LUNs really survive the loss of one path, one HBA
/// or one array target port (M8.6), judged as findings about the cause.
/// </summary>
/// <remarks>
/// <para>
/// Moved from the <c>multipath-single-point-of-failure</c> alarm (ADR-0024).
/// The subject is what the operator fixes (K1 note §3.1): a second HBA is
/// fitted once per host, so <see cref="PathCase.SingleHba"/> is one finding per
/// HBA however many devices depend on it; zoning a second array port is once
/// per port, so <see cref="PathCase.SingleTarget"/> is one per target WWPN; only
/// <see cref="PathCase.SinglePath"/> is per device, because one LUN presented
/// over one path really is a per-device cause.
/// </para>
/// <para>
/// The device-level judgement is the alarm's, unchanged: only devices a VMFS
/// datastore lives on, and only those seen by at least two hosts, so a host's
/// own local VMFS never qualifies; a device with one path is the single-path
/// case and nothing else; the single-HBA case needs exactly one named working
/// adapter, is not judged when every path is iSCSI (port binding's redundancy
/// is not visible) nor when a path is dead (that is the path-redundancy
/// alarm's fact); the target case is reached only when neither of those fired
/// and every working path names its target.
/// </para>
/// <para>
/// Every HBA and every target port carrying a working path to a shared device
/// gets a verdict, Passing included, so a fix is a recorded transition. One
/// that has no device this product can judge is <c>NotEvaluated</c> with the
/// reason. A host in maintenance mode keeps its subjects but is not judged:
/// its verdicts come back <c>NotEvaluated</c>, so its acceptances stay on the
/// rows until it returns.
/// </para>
/// <para>
/// Sharing counts hosts that are active or in maintenance: a LUN does not stop
/// being shared because a peer was put into maintenance. Vanished hosts are
/// not counted.
/// </para>
/// </remarks>
public sealed class MultipathCheck(MultipathCheck.PathCase judges) : IComplianceCheck
{
    public enum PathCase
    {
        SinglePath,
        SingleHba,
        SingleTarget,
    }

    private static readonly string[] WorkingStates = ["active", "standby"];

    /// <summary>vim25's transport type for iSCSI paths, software or hardware alike.</summary>
    private const string IscsiTransport = "HostInternetScsiTargetTransport";

    private static readonly ConditionalWeakTable<EntityGraph, SharedDevices> Shared = [];

    public PathCase Judges { get; } = judges;

    public EntityKind AppliesTo => EntityKind.EsxiHost;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        var shared = Shared.GetValue(context.Graph, SharedDevices.Of);
        var devices = Devices(entity, shared);

        var verdicts = Judges switch
        {
            PathCase.SinglePath => SinglePath(devices),
            PathCase.SingleHba => SingleHba(devices),
            PathCase.SingleTarget => SingleTarget(devices),
            _ => [],
        };

        if (entity.ObservationState == ObservationState.InMaintenance)
        {
            return
            [
                .. verdicts.Select(v => v with
                {
                    Verdict = ComplianceVerdict.NotEvaluated,
                    Reason = "The host is in maintenance mode; its paths are judged again when it returns " +
                             "to service.",
                }),
            ];
        }

        return verdicts;
    }

    // --- device-level judgement ---------------------------------------------

    private enum Outcome
    {
        Single,
        Redundant,
        Unjudged,
    }

    private sealed record Device(
        string Naa,
        string Datastore,
        IReadOnlyList<StoragePath> Paths,
        Outcome Hba,
        string? HbaName,
        string? HbaReason,
        Outcome Target,
        string? TargetName,
        string? TargetReason)
    {
        public IEnumerable<StoragePath> Working => Paths.Where(IsWorking);
    }

    private static bool IsWorking(StoragePath path) =>
        WorkingStates.Contains(path.State, StringComparer.OrdinalIgnoreCase);

    private static string Identify(StoragePath path) =>
        path.StorageDeviceId.Length > 0 ? path.StorageDeviceId : path.DeviceKey;

    private static List<Device> Devices(Entity host, SharedDevices shared) =>
    [
        .. host.StoragePaths
            .Where(p => Identify(p).Length > 0)
            .GroupBy(Identify, StringComparer.OrdinalIgnoreCase)
            .Where(g => shared.Datastores.ContainsKey(g.Key) && shared.SeenBy.GetValueOrDefault(g.Key) >= 2)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => Judge(g.Key, shared.Datastores[g.Key], [.. g])),
    ];

    private static Device Judge(string naa, string datastore, List<StoragePath> paths)
    {
        var (hba, hbaName, hbaReason) = HbaOutcome(paths);

        var (target, targetName, targetReason) = paths.Count == 1
            ? (Outcome.Unjudged, (string?)null, "one path; see " + PathSingle)
            : hba == Outcome.Single
                ? (Outcome.Unjudged, null, $"already fails {PathSingleHba}")
                : TargetOutcome(paths);

        return new Device(naa, datastore, paths, hba, hbaName, hbaReason, target, targetName, targetReason);
    }

    private static (Outcome, string?, string?) HbaOutcome(List<StoragePath> paths)
    {
        if (paths.Count == 1)
        {
            return (Outcome.Unjudged, null, "one path; see " + PathSingle);
        }

        var adapters = paths.Where(IsWorking)
            .Select(p => p.Adapter)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (adapters.Count == 0)
        {
            return (Outcome.Unjudged, null, "no working path names its adapter");
        }

        if (adapters.Count > 1)
        {
            return (Outcome.Redundant, null, null);
        }

        if (paths.All(p => string.Equals(p.Transport, IscsiTransport, StringComparison.Ordinal)))
        {
            return (Outcome.Unjudged, null,
                "every path is iSCSI; software iSCSI port binding's redundancy is in the bound NICs, " +
                "which are not visible here");
        }

        if (paths.Any(p => !IsWorking(p)))
        {
            return (Outcome.Unjudged, null,
                "a path is not working; that is reported by the storage path redundancy alarm");
        }

        return (Outcome.Single, adapters[0], null);
    }

    private static (Outcome, string?, string?) TargetOutcome(List<StoragePath> paths)
    {
        var working = paths.Where(IsWorking).ToList();

        if (working.Count < 2)
        {
            return (Outcome.Unjudged, null, "fewer than two working paths");
        }

        if (working.Any(p => string.IsNullOrEmpty(p.Target)))
        {
            return (Outcome.Unjudged, null, "a working path names no target port");
        }

        var targets = working.Select(p => p.Target!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return targets.Count == 1
            ? (Outcome.Single, targets[0], null)
            : (Outcome.Redundant, null, null);
    }

    // --- the three cases ----------------------------------------------------

    private static List<CheckVerdict> SinglePath(List<Device> devices)
    {
        const string expected = "more than one path";

        return
        [
            .. devices.Select(d => d.Paths.Count == 1
                ? Verdict(ComplianceVerdict.Failing, expected,
                    $"one path, {d.Paths[0].Name} on adapter " +
                    $"{(d.Paths[0].Adapter.Length > 0 ? d.Paths[0].Adapter : "not reported")}",
                    subject: d.Naa, label: $"{d.Naa} (datastore {d.Datastore})")
                : Verdict(ComplianceVerdict.Passing, expected, $"{d.Paths.Count} paths",
                    subject: d.Naa, label: $"{d.Naa} (datastore {d.Datastore})")),
        ];
    }

    private static List<CheckVerdict> SingleHba(List<Device> devices)
    {
        const string expected = "every shared device has a working path through another HBA too";

        var adapters = devices
            .SelectMany(d => d.Working.Select(p => p.Adapter))
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var verdicts = new List<CheckVerdict>();

        foreach (var adapter in adapters)
        {
            var through = devices
                .Where(d => d.Working.Any(p => string.Equals(p.Adapter, adapter, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var single = through
                .Where(d => d.Hba == Outcome.Single &&
                            string.Equals(d.HbaName, adapter, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Naa)
                .ToList();

            if (single.Count > 0)
            {
                verdicts.Add(Verdict(ComplianceVerdict.Failing, expected,
                    $"all working paths of {single.Count} shared device(s) go through {adapter}: " +
                    FirstFew(single),
                    subject: adapter));
            }
            else if (through.Any(d => d.Hba == Outcome.Redundant))
            {
                verdicts.Add(Verdict(ComplianceVerdict.Passing, expected,
                    $"{through.Count(d => d.Hba == Outcome.Redundant)} shared device(s) through {adapter} " +
                    "also leave through another HBA",
                    subject: adapter));
            }
            else
            {
                verdicts.Add(Verdict(ComplianceVerdict.NotEvaluated, expected,
                    reason: "No shared device through this HBA could be judged: " +
                            through.First().HbaReason + '.',
                    subject: adapter));
            }
        }

        return verdicts;
    }

    private static List<CheckVerdict> SingleTarget(List<Device> devices)
    {
        const string expected = "every shared device has working paths to at least two target ports";

        var targets = devices
            .SelectMany(d => d.Working.Select(p => p.Target))
            .OfType<string>()
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var verdicts = new List<CheckVerdict>();

        foreach (var target in targets)
        {
            var reaching = devices
                .Where(d => d.Working.Any(p => string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var single = reaching
                .Where(d => d.Target == Outcome.Single &&
                            string.Equals(d.TargetName, target, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Naa)
                .ToList();

            if (single.Count > 0)
            {
                verdicts.Add(Verdict(ComplianceVerdict.Failing, expected,
                    $"all working paths of {single.Count} shared device(s) land on this port: " +
                    FirstFew(single),
                    subject: target));
            }
            else if (reaching.Any(d => d.Target == Outcome.Redundant))
            {
                verdicts.Add(Verdict(ComplianceVerdict.Passing, expected,
                    $"{reaching.Count(d => d.Target == Outcome.Redundant)} shared device(s) reaching this " +
                    "port also reach another",
                    subject: target));
            }
            else
            {
                verdicts.Add(Verdict(ComplianceVerdict.NotEvaluated, expected,
                    reason: "No shared device reaching this port could be judged: " +
                            reaching.First().TargetReason + '.',
                    subject: target));
            }
        }

        return verdicts;
    }

    /// <summary>The VMFS devices of the estate and how many hosts reach each.</summary>
    private sealed record SharedDevices(
        Dictionary<string, string> Datastores,
        Dictionary<string, int> SeenBy)
    {
        public static SharedDevices Of(EntityGraph graph)
        {
            var datastores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var datastore in graph.Entities.Values.Where(IsVmfsDatastore))
            {
                foreach (var mark in datastore.Marks.Where(m =>
                             m.Kind == IdentityMarkKind.StorageDeviceId && m.Value.Length > 0))
                {
                    datastores[mark.Value] = datastore.DisplayName;
                }
            }

            var seenBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var host in graph.Entities.Values.Where(e =>
                         e.Kind == EntityKind.EsxiHost &&
                         e.ObservationState is ObservationState.Active or ObservationState.InMaintenance))
            {
                foreach (var id in host.StoragePaths.Select(Identify).Where(id => id.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    seenBy[id] = seenBy.GetValueOrDefault(id) + 1;
                }
            }

            return new SharedDevices(datastores, seenBy);
        }

        private static bool IsVmfsDatastore(Entity entity) =>
            entity.Kind == EntityKind.Datastore &&
            entity.ObservationState != ObservationState.Vanished &&
            entity.Settings.TryGetValue("type", out var type) &&
            string.Equals(type, "VMFS", StringComparison.OrdinalIgnoreCase);
    }
}
