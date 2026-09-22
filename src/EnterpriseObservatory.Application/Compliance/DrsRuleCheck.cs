using System.Runtime.CompilerServices;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// Whether DRS placed virtual machines as each of the cluster's own affinity,
/// anti-affinity and VM-host rules says (M8.3), judged as a finding per rule.
/// </summary>
/// <remarks>
/// <para>
/// Moved from the <c>drs-rule-violation</c> alarm (ADR-0024). The subject is
/// the rule's <c>ruleUuid</c> — the name only when vCenter reports no uuid —
/// and the name travels in <see cref="CheckVerdict.SubjectLabel"/>, never in
/// <see cref="CheckVerdict.Observed"/>, so renaming a rule keeps the finding
/// and its acceptance.
/// </para>
/// <para>
/// Placement is read from the graph's own <c>RunsOn</c> edges, not from
/// vCenter's <c>inCompliance</c> flag: a verdict without its evidence cannot
/// be audited. A VM the collector reported as not powered on is left out —
/// DRS does not place what is not running — and a VM whose power state was
/// not collected is kept, because silence about power is not evidence it is
/// off.
/// </para>
/// <para>
/// A rule this product cannot confirm either way is <c>NotEvaluated</c> with
/// the reason: disabled (DRS does not enforce it), no placement evidence, or
/// vCenter says out of compliance and the placement cannot confirm it. A
/// cluster with no rules has no subject and returns nothing; one whose
/// configuration was not read returns one <c>NotEvaluated</c> verdict.
/// </para>
/// </remarks>
public sealed class DrsRuleCheck : IComplianceCheck
{
    private const string PowerStateSetting = "powerState";
    private const string PoweredOnValue = "poweredOn";

    private static readonly ConditionalWeakTable<EntityGraph, Dictionary<EntityId, EntityId>> Placements = [];

    public EntityKind AppliesTo => EntityKind.Cluster;

    public IReadOnlyList<CheckVerdict> Judge(ComplianceControl control, Entity entity, CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        if (!ConfigurationRead(entity))
        {
            return
            [
                Verdict(ComplianceVerdict.NotEvaluated, "every DRS rule honoured", reason:
                    "DRS rules not read: this cluster's configuration (configurationEx) was not read."),
            ];
        }

        var vmHost = Placements.GetValue(context.Graph, VmHosts);

        return [.. entity.DrsRules.Select(rule => Judge(context.Graph, rule, vmHost))];
    }

    private static CheckVerdict Judge(EntityGraph graph, DrsRule rule, Dictionary<EntityId, EntityId> vmHost)
    {
        var subject = string.IsNullOrWhiteSpace(rule.RuleUuid) ? rule.Name : rule.RuleUuid;
        var expected = Expected(rule);

        CheckVerdict Of(ComplianceVerdict verdict, string? observed = null, string? reason = null) =>
            Verdict(verdict, expected, observed, reason, subject, rule.Name);

        var placement = PlacementOf(rule, vmHost);
        var observed = Describe(graph, rule, placement);

        if (!rule.Enabled)
        {
            return Of(ComplianceVerdict.NotEvaluated, observed,
                "The rule is disabled: DRS does not enforce it, so there is nothing it failed to honour.");
        }

        bool? violated = rule.Kind switch
        {
            DrsRuleKind.Affinity => AffinityViolated(placement),
            DrsRuleKind.AntiAffinity => AntiAffinityViolated(placement),
            DrsRuleKind.VmHostAffine => VmHostViolated(rule, placement, mustBeInGroup: true),
            DrsRuleKind.VmHostAntiAffine => VmHostViolated(rule, placement, mustBeInGroup: false),
            _ => null,
        };

        return violated switch
        {
            true => Of(ComplianceVerdict.Failing, observed),
            false => Of(ComplianceVerdict.Passing, observed),
            null when rule.VCenterInCompliance == false => Of(ComplianceVerdict.NotEvaluated, observed,
                "vCenter reports this rule out of compliance, but the placement this product reads " +
                "cannot confirm it — most likely some of its virtual machines are powered off or not " +
                "collected yet."),
            null => Of(ComplianceVerdict.NotEvaluated, observed, NoEvidenceReason(rule)),
        };
    }

    private static string NoEvidenceReason(DrsRule rule) =>
        rule.Kind is DrsRuleKind.VmHostAffine or DrsRuleKind.VmHostAntiAffine && rule.HostEntityIds.Count == 0
            ? "The host group this rule names could not be resolved, so placement cannot be checked against it."
            : "Not enough of this rule's virtual machines are known to be running to judge their placement.";

    private static string Expected(DrsRule rule)
    {
        var kind = rule.Kind switch
        {
            DrsRuleKind.Affinity => "its virtual machines on the same host",
            DrsRuleKind.AntiAffinity => "its virtual machines on different hosts",
            DrsRuleKind.VmHostAffine => "its virtual machines only on hosts in its host group",
            DrsRuleKind.VmHostAntiAffine => "its virtual machines off hosts in its host group",
            _ => "placement as the rule says",
        };

        return $"{kind} ({(rule.Mandatory ? "mandatory" : "preferred")})";
    }

    /// <summary>Every powered-on (or power-unknown) VM's current host.</summary>
    private static Dictionary<EntityId, EntityId> VmHosts(EntityGraph graph)
    {
        var map = new Dictionary<EntityId, EntityId>();

        foreach (var edge in graph.Relationships.Where(r => r.Kind == RelationshipKind.RunsOn))
        {
            if (graph.Entities.TryGetValue(edge.From, out var vm) &&
                vm.Settings.TryGetValue(PowerStateSetting, out var power) &&
                !string.Equals(power, PoweredOnValue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            map[edge.From] = edge.To;
        }

        return map;
    }

    private readonly record struct Placement(IReadOnlyDictionary<EntityId, EntityId> Known, int TotalMembers)
    {
        public bool Complete => Known.Count == TotalMembers && TotalMembers > 0;
    }

    private static Placement PlacementOf(DrsRule rule, Dictionary<EntityId, EntityId> vmHost)
    {
        var known = new Dictionary<EntityId, EntityId>();

        foreach (var vm in rule.VirtualMachineEntityIds.Select(v => new EntityId(v)))
        {
            if (vmHost.TryGetValue(vm, out var host))
            {
                known[vm] = host;
            }
        }

        return new Placement(known, rule.VirtualMachineEntityIds.Count);
    }

    private static bool? AffinityViolated(Placement placement)
    {
        if (placement.Known.Count < 2 && !placement.Complete)
        {
            return null;
        }

        if (placement.Known.Values.Distinct().Count() >= 2)
        {
            return true;
        }

        return placement.Complete ? false : null;
    }

    private static bool? AntiAffinityViolated(Placement placement)
    {
        if (placement.Known.Count < 2 && !placement.Complete)
        {
            return null;
        }

        if (placement.Known.Values.GroupBy(h => h).Any(g => g.Count() >= 2))
        {
            return true;
        }

        return placement.Complete ? false : null;
    }

    private static bool? VmHostViolated(DrsRule rule, Placement placement, bool mustBeInGroup)
    {
        if (rule.HostEntityIds.Count == 0 || placement.Known.Count == 0)
        {
            return null;
        }

        var group = rule.HostEntityIds.Select(h => new EntityId(h)).ToHashSet();

        if (placement.Known.Values.Any(host => mustBeInGroup != group.Contains(host)))
        {
            return true;
        }

        return placement.Complete ? false : null;
    }

    /// <summary>Where the rule's VMs run now — evidence, never the rule's name.</summary>
    private static string Describe(EntityGraph graph, DrsRule rule, Placement placement)
    {
        var where = placement.Known.Count == 0
            ? "no named virtual machine is known to be running"
            : string.Join("; ", placement.Known
                .Select(kv => $"{Name(graph, kv.Key)} on {Name(graph, kv.Value)}")
                .Order(StringComparer.OrdinalIgnoreCase));

        var vCenter = rule.VCenterInCompliance switch
        {
            true => "; vCenter: in compliance",
            false => "; vCenter: out of compliance",
            null => string.Empty,
        };

        return where + vCenter;
    }

    private static string Name(EntityGraph graph, EntityId id) =>
        graph.Entities.TryGetValue(id, out var entity) ? entity.DisplayName : id.Value;
}
