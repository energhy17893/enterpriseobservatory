using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Says that DRS placed virtual machines against one of the cluster's own
/// affinity, anti-affinity or VM-host rules.
/// </summary>
/// <remarks>
/// <para>
/// Roadmap M8.3. The rules themselves are collected by
/// <c>VsphereInventorySource</c> from <c>configurationEx.rule</c> and
/// <c>configurationEx.group</c> and carried on the cluster entity as
/// <see cref="Entity.DrsRules"/> — see <see cref="DrsRule"/> for the vim25
/// types each rule kind traces back to (developer.broadcom.com, vSphere Web
/// Services API reference, <c>vim.cluster.RuleInfo</c> and its subtypes).
/// This rule reads none of that wire detail; it reads only the resolved rule
/// and where the graph says each VM is actually running (<c>RunsOn</c>).
/// </para>
/// <para>
/// vCenter already computes its own <c>inCompliance</c> flag per rule and
/// this rule does not defer to it, for the same reason <c>StoragePath</c>'s
/// domain judgement and vROps's health colour are kept apart elsewhere in
/// this product: a flag reported without its evidence cannot be audited, and
/// a flag that is wrong cannot be caught. What is computed here from the
/// graph's own <c>RunsOn</c> edges can be — and when the two disagree, that
/// disagreement is itself worth an operator's attention, so it is reported
/// rather than silently trusted either way. See <see cref="Verdict"/>.
/// </para>
/// <para>
/// Two verdicts, not one intensity. A rule DRS is actively enforcing and has
/// nonetheless failed to satisfy is a fault; a rule vCenter reports as out of
/// compliance while this product's own placement evidence cannot confirm it
/// is a gap in that evidence, not a fault, and is reported at
/// <see cref="AlertSeverity.Info"/> rather than invented a severity for.
/// </para>
/// <para>
/// Disabled rules — vim25 <c>enabled = false</c> — are not enforced by DRS at
/// all, so a placement that would violate one is not a fault DRS failed to
/// prevent; it is informational, the same way a syslog target nobody
/// configured is not a misconfiguration. See <see cref="Classify"/>.
/// </para>
/// <para>
/// Severity otherwise follows what each rule kind actually protects.
/// Anti-affinity and VM-host rules exist to keep single points of failure
/// apart — two nodes of a database cluster, a VM and the host carrying its
/// licence dongle — and a mandatory one that DRS could not honour is exactly
/// the redundancy assumption an operator is relying on turning out false.
/// A plain affinity rule's job is performance (co-locating chatty VMs to
/// avoid a network hop), and failing to honour it costs latency rather than
/// availability, so it is kept at <see cref="AlertSeverity.Warning"/>
/// regardless of <c>mandatory</c>.
/// </para>
/// </remarks>
public static class DrsRuleViolations
{
    /// <summary>Names this rule in a failure alert. Stable across releases.</summary>
    public const string RuleId = "drs-rule-violation";

    public const string Category = "DRS";

    private const string Platform = "platform";

    private const string AffinityTitle = "DRS affinity rule violated";
    private const string AntiAffinityTitle = "DRS anti-affinity rule violated";
    private const string VmHostAffineTitle = "DRS VM-host affinity rule violated";
    private const string VmHostAntiAffineTitle = "DRS VM-host anti-affinity rule violated";

    /// <summary>
    /// vCenter reports the rule out of compliance and this product's own
    /// placement evidence does not confirm why.
    /// </summary>
    private const string DisagreementTitle = "DRS rule reported out of compliance";

    /// <summary>
    /// Every DRS rule violation the graph's current placement evidences.
    /// </summary>
    /// <param name="graph">
    /// The graph as the cycle merged it. Only clusters carrying rules are
    /// judged, and placement is read from this same graph's <c>RunsOn</c>
    /// edges so a rule is never judged against a VM's previous host.
    /// </param>
    public static IReadOnlyList<AlertDefinition> Evaluate(EntityGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var alerts = new List<AlertDefinition>();
        var vmHost = VmHosts(graph);

        foreach (var cluster in graph.Active.Where(Judgeable))
        {
            foreach (var rule in cluster.DrsRules)
            {
                if (Verdict(graph, cluster, rule, vmHost) is { } alert)
                {
                    alerts.Add(alert);
                }
            }
        }

        return alerts;
    }

    private static bool Judgeable(Entity entity) =>
        entity.Kind == EntityKind.Cluster && entity.DrsRules.Count > 0;

    /// <summary>Every VM's current host, from this cycle's <c>RunsOn</c> edges.</summary>
    private static Dictionary<EntityId, EntityId> VmHosts(EntityGraph graph)
    {
        var map = new Dictionary<EntityId, EntityId>();

        foreach (var edge in graph.Relationships.Where(r => r.Kind == RelationshipKind.RunsOn))
        {
            map[edge.From] = edge.To;
        }

        return map;
    }

    /// <summary>
    /// What one rule adds up to, or null when there is nothing to report.
    /// </summary>
    /// <remarks>
    /// A rule with no evidence to judge from — every one of its VMs powered
    /// off, unplaced, or the host/VM group it names could not be resolved —
    /// says nothing, in either direction. That is not a claim that the rule
    /// is satisfied; it is silence, the same silence an unreadable path table
    /// produces in <see cref="StoragePathRedundancy"/>.
    /// </remarks>
    private static AlertDefinition? Verdict(
        EntityGraph graph, Entity cluster, DrsRule rule, Dictionary<EntityId, EntityId> vmHost)
    {
        var placement = PlacementOf(rule, vmHost);

        var violated = rule.Kind switch
        {
            DrsRuleKind.Affinity => AffinityViolated(rule, placement),
            DrsRuleKind.AntiAffinity => AntiAffinityViolated(rule, placement),
            DrsRuleKind.VmHostAffine => VmHostViolated(rule, placement, mustBeInGroup: true),
            DrsRuleKind.VmHostAntiAffine => VmHostViolated(rule, placement, mustBeInGroup: false),
            _ => (bool?)null,
        };

        // vCenter says this rule is unsatisfied and the graph's own placement
        // evidence cannot say why — worth reporting, but as a gap in this
        // product's visibility rather than as a fault it has found itself.
        var disagreementOnly = violated is not true && rule.VCenterInCompliance == false;

        if (violated is not true && !disagreementOnly)
        {
            return null;
        }

        var (title, severity) = Classify(rule, violated is true);

        return Alert(cluster, rule, title, severity, Describe(graph, cluster, rule, violated, placement));
    }

    /// <summary>This rule's VMs that are currently known to be running somewhere.</summary>
    /// <remarks>
    /// A VM in the rule's list with no <c>RunsOn</c> edge — powered off, or
    /// simply not (yet) collected — is left out rather than treated as
    /// evidence of anything. <see cref="AffinityViolated"/> and its siblings
    /// use <see cref="Placement.Complete"/> to tell "every VM we could place
    /// agrees" from "some VMs we could not place might disagree".
    /// </remarks>
    private static Placement PlacementOf(DrsRule rule, Dictionary<EntityId, EntityId> vmHost)
    {
        var known = new Dictionary<EntityId, EntityId>();

        foreach (var vm in rule.VirtualMachineEntityIds)
        {
            if (vmHost.TryGetValue(new EntityId(vm), out var host))
            {
                known[new EntityId(vm)] = host;
            }
        }

        return new Placement(known, TotalMembers: rule.VirtualMachineEntityIds.Count);
    }

    private readonly record struct Placement(IReadOnlyDictionary<EntityId, EntityId> Known, int TotalMembers)
    {
        /// <summary>Every member the rule names has a known current host.</summary>
        public bool Complete => Known.Count == TotalMembers && TotalMembers > 0;
    }

    /// <remarks>
    /// True the moment two known placements land on different hosts — that is
    /// already a violation whatever the rest of the group turns out to be
    /// doing. False only once every member is known and they all agree.
    /// </remarks>
    private static bool? AffinityViolated(DrsRule rule, Placement placement)
    {
        if (placement.Known.Count < 2 && !placement.Complete)
        {
            return null;
        }

        var distinctHosts = placement.Known.Values.Distinct().Count();

        if (distinctHosts >= 2)
        {
            return true;
        }

        return placement.Complete ? false : null;
    }

    /// <remarks>
    /// True the moment two of the rule's VMs share a host — again a violation
    /// regardless of the rest of the group. False only once every member is
    /// known and no two share one.
    /// </remarks>
    private static bool? AntiAffinityViolated(DrsRule rule, Placement placement)
    {
        if (placement.Known.Count < 2 && !placement.Complete)
        {
            return null;
        }

        var sharesAHost = placement.Known.Values
            .GroupBy(h => h)
            .Any(g => g.Count() >= 2);

        if (sharesAHost)
        {
            return true;
        }

        return placement.Complete ? false : null;
    }

    /// <remarks>
    /// The host group is read from <see cref="DrsRule.HostEntityIds"/>, which
    /// is already resolved by the collector; an empty one means the group
    /// could not be resolved (deleted, or unreadable) and there is nothing to
    /// compare placement against.
    /// </remarks>
    private static bool? VmHostViolated(DrsRule rule, Placement placement, bool mustBeInGroup)
    {
        if (rule.HostEntityIds.Count == 0 || placement.Known.Count == 0)
        {
            return null;
        }

        var hostGroup = rule.HostEntityIds.Select(h => new EntityId(h)).ToHashSet();

        var anyOut = placement.Known.Values.Any(host => mustBeInGroup != hostGroup.Contains(host));

        if (anyOut)
        {
            return true;
        }

        return placement.Complete ? false : null;
    }

    private static (string Title, AlertSeverity Severity) Classify(DrsRule rule, bool violated)
    {
        if (!violated)
        {
            return (DisagreementTitle, AlertSeverity.Info);
        }

        var title = rule.Kind switch
        {
            DrsRuleKind.Affinity => AffinityTitle,
            DrsRuleKind.AntiAffinity => AntiAffinityTitle,
            DrsRuleKind.VmHostAffine => VmHostAffineTitle,
            DrsRuleKind.VmHostAntiAffine => VmHostAntiAffineTitle,
            _ => AffinityTitle,
        };

        if (!rule.Enabled)
        {
            // Not enforced, so nothing DRS failed to honour. See the class
            // remarks for why this is informational rather than absent.
            return (title, AlertSeverity.Info);
        }

        var severity = rule.Kind switch
        {
            DrsRuleKind.AntiAffinity or DrsRuleKind.VmHostAffine or DrsRuleKind.VmHostAntiAffine =>
                rule.Mandatory ? AlertSeverity.Critical : AlertSeverity.Warning,
            _ => AlertSeverity.Warning,
        };

        return (title, severity);
    }

    private static AlertDefinition Alert(
        Entity cluster, DrsRule rule, string title, AlertSeverity severity, string description) =>
        new()
        {
            // Cluster and rule name together: a rule's name is unique within
            // its cluster (vim25 enforces it) but not across a multi-cluster
            // estate, and the title alone would merge two clusters' same
            // rule name into one fingerprint.
            Fingerprint = AlertFingerprint.Create(
                Attribution(cluster), title, Category, $"{cluster.Id.Value}/{rule.Name}", RuleId),
            Severity = severity,
            Title = title,
            Description = description,
            Category = Category,
            Source = Attribution(cluster),
            Entity = cluster.Id,
        };

    private static string Attribution(Entity cluster) =>
        string.IsNullOrWhiteSpace(cluster.SourceInstanceId) ? Platform : cluster.SourceInstanceId;

    private static string Describe(
        EntityGraph graph, Entity cluster, DrsRule rule, bool? violated, Placement placement)
    {
        var kind = rule.Kind switch
        {
            DrsRuleKind.Affinity => "keep its virtual machines on the same host",
            DrsRuleKind.AntiAffinity => "keep its virtual machines on different hosts",
            DrsRuleKind.VmHostAffine => "run its virtual machines only on hosts in its host group",
            DrsRuleKind.VmHostAntiAffine => "keep its virtual machines off hosts in its host group",
            _ => "constrain virtual machine placement",
        };

        var strength = rule.Mandatory ? "mandatory" : "preferred (\"should\")";
        var state = rule.Enabled ? "enabled" : "disabled";

        var placements = placement.Known.Count == 0
            ? "No named virtual machine is currently known to be running, so nothing about " +
              "today's placement could be checked."
            : "Currently: " + string.Join(
                "; ", placement.Known.Select(kv =>
                    $"{Name(graph, kv.Key)} on {Name(graph, kv.Value)}")) + '.';

        var groupNote = rule.Kind is DrsRuleKind.VmHostAffine or DrsRuleKind.VmHostAntiAffine
            ? rule.HostEntityIds.Count == 0
                ? " The host group this rule names could not be resolved, so placement could not " +
                  "be checked against it."
                : $" The host group's current members: {string.Join(", ", rule.HostEntityIds.Select(h => Name(graph, new EntityId(h))))}."
            : string.Empty;

        var vCenterNote = rule.VCenterInCompliance switch
        {
            true when violated is true =>
                " vCenter currently reports this rule as in compliance, which disagrees with the " +
                "placement above; the two may be reading the cluster at different moments.",
            false when violated is not true =>
                " vCenter reports this rule as out of compliance; this product's own placement " +
                "evidence above does not confirm a violation, most likely because some of the " +
                "rule's virtual machines are powered off or not yet collected.",
            false when violated is true => " vCenter agrees: it also reports this rule out of compliance.",
            true when violated is false => " vCenter agrees: it also reports this rule in compliance.",
            _ => string.Empty,
        };

        var verdictSentence = violated is true
            ? $"is {strength} and DRS has not honoured it."
            : "vCenter reports as out of compliance, though placement alone does not confirm it.";

        return $"Cluster '{cluster.DisplayName}''s {state} rule '{rule.Name}' should {kind}, and " +
               $"{verdictSentence} {placements}{groupNote}{vCenterNote}";
    }

    private static string Name(EntityGraph graph, EntityId id) =>
        graph.Entities.TryGetValue(id, out var entity) ? entity.DisplayName : id.Value;
}
