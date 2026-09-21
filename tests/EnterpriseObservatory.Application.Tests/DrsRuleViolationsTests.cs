using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Roadmap M8.3. Placement, and what a cluster's DRS rules say about it.
/// </summary>
public class DrsRuleViolationsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Source = "vc-1";
    private const string ClusterId = "vc-1:domain-c7";
    private const string Vm1 = "vc-1:vm-101";
    private const string Vm2 = "vc-1:vm-102";
    private const string HostA = "vc-1:host-11";
    private const string HostB = "vc-1:host-12";

    private const string PowerStateSetting = "powerState";

    private static Entity Node(string id, EntityKind kind) => new()
    {
        Id = new EntityId(id),
        Kind = kind,
        DisplayName = id,
        SourceInstanceId = Source,
        LastSeenUtc = T0,
    };

    private static Entity PoweredOffVm(string id) => Node(id, EntityKind.VirtualMachine) with
    {
        Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PowerStateSetting] = "poweredOff",
        },
    };

    private static Entity Cluster(params DrsRule[] rules) => new()
    {
        Id = new EntityId(ClusterId),
        Kind = EntityKind.Cluster,
        DisplayName = "prod-cluster",
        SourceInstanceId = Source,
        LastSeenUtc = T0,
        DrsRules = rules,
    };

    private static Relationship RunsOn(string vm, string host) => new()
    {
        From = new EntityId(vm),
        To = new EntityId(host),
        Kind = RelationshipKind.RunsOn,
        ObservedAtUtc = T0,
    };

    /// <summary>A cluster carrying the given rules, plus named VMs placed on named hosts.</summary>
    private static EntityGraph Graph(DrsRule[] rules, params (string Vm, string Host)[] placements)
    {
        var hosts = placements.Select(p => p.Host).Distinct().Select(h => Node(h, EntityKind.EsxiHost));
        var vms = placements.Select(p => p.Vm).Distinct().Select(v => Node(v, EntityKind.VirtualMachine));

        return new EntityGraph
        {
            Entities = new[] { Cluster(rules) }.Concat(hosts).Concat(vms).ToDictionary(e => e.Id),
            Relationships = [.. placements.Select(p => RunsOn(p.Vm, p.Host))],
        };
    }

    private static DrsRule Affinity(
        string name = "keep-together", bool enabled = true, bool mandatory = false, bool? inCompliance = null) =>
        new()
        {
            Name = name,
            Kind = DrsRuleKind.Affinity,
            Enabled = enabled,
            Mandatory = mandatory,
            VCenterInCompliance = inCompliance,
            VirtualMachineEntityIds = [Vm1, Vm2],
        };

    private static DrsRule AntiAffinity(
        string name = "keep-apart", bool enabled = true, bool mandatory = true, bool? inCompliance = null) =>
        new()
        {
            Name = name,
            Kind = DrsRuleKind.AntiAffinity,
            Enabled = enabled,
            Mandatory = mandatory,
            VCenterInCompliance = inCompliance,
            VirtualMachineEntityIds = [Vm1, Vm2],
        };

    private static DrsRule VmHostAffine(
        string name = "must-run-on-a", bool enabled = true, bool mandatory = true, bool? inCompliance = null) =>
        new()
        {
            Name = name,
            Kind = DrsRuleKind.VmHostAffine,
            Enabled = enabled,
            Mandatory = mandatory,
            VCenterInCompliance = inCompliance,
            VirtualMachineEntityIds = [Vm1],
            HostEntityIds = [HostA],
        };

    private static DrsRule VmHostAntiAffine(
        string name = "must-not-run-on-a", bool enabled = true, bool mandatory = true, bool? inCompliance = null) =>
        new()
        {
            Name = name,
            Kind = DrsRuleKind.VmHostAntiAffine,
            Enabled = enabled,
            Mandatory = mandatory,
            VCenterInCompliance = inCompliance,
            VirtualMachineEntityIds = [Vm1],
            HostEntityIds = [HostA],
        };

    [Fact]
    public void Affinity_split_across_hosts_is_reported()
    {
        var rule = Affinity();
        var graph = Graph([rule], (Vm1, HostA), (Vm2, HostB));

        var alert = Assert.Single(DrsRuleViolations.Evaluate(graph));

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal("DRS affinity rule violated", alert.Title);
        Assert.Equal(new EntityId(ClusterId), alert.Entity);
    }

    [Fact]
    public void Affinity_kept_on_the_same_host_is_silent()
    {
        var rule = Affinity();
        var graph = Graph([rule], (Vm1, HostA), (Vm2, HostA));

        Assert.Empty(DrsRuleViolations.Evaluate(graph));
    }

    [Fact]
    public void Anti_affinity_co_located_on_one_host_is_reported_as_critical_when_mandatory()
    {
        var rule = AntiAffinity(mandatory: true);
        var graph = Graph([rule], (Vm1, HostA), (Vm2, HostA));

        var alert = Assert.Single(DrsRuleViolations.Evaluate(graph));

        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("DRS anti-affinity rule violated", alert.Title);
    }

    [Fact]
    public void Anti_affinity_violation_is_warning_when_the_rule_is_only_a_preference()
    {
        var rule = AntiAffinity(mandatory: false);
        var graph = Graph([rule], (Vm1, HostA), (Vm2, HostA));

        var alert = Assert.Single(DrsRuleViolations.Evaluate(graph));

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
    }

    [Fact]
    public void Anti_affinity_kept_apart_is_silent()
    {
        var rule = AntiAffinity();
        var graph = Graph([rule], (Vm1, HostA), (Vm2, HostB));

        Assert.Empty(DrsRuleViolations.Evaluate(graph));
    }

    [Fact]
    public void Must_run_on_violated_when_the_vm_is_placed_off_the_host_group()
    {
        var rule = VmHostAffine(mandatory: true);
        var graph = Graph([rule], (Vm1, HostB));

        var alert = Assert.Single(DrsRuleViolations.Evaluate(graph));

        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("DRS VM-host affinity rule violated", alert.Title);
    }

    [Fact]
    public void Should_run_on_violated_is_a_warning_rather_than_critical()
    {
        var rule = VmHostAffine(mandatory: false);
        var graph = Graph([rule], (Vm1, HostB));

        var alert = Assert.Single(DrsRuleViolations.Evaluate(graph));

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
    }

    [Fact]
    public void Must_run_on_satisfied_is_silent()
    {
        var rule = VmHostAffine();
        var graph = Graph([rule], (Vm1, HostA));

        Assert.Empty(DrsRuleViolations.Evaluate(graph));
    }

    [Fact]
    public void Must_not_run_on_violated_when_the_vm_is_placed_on_the_forbidden_group()
    {
        var rule = VmHostAntiAffine(mandatory: true);
        var graph = Graph([rule], (Vm1, HostA));

        var alert = Assert.Single(DrsRuleViolations.Evaluate(graph));

        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("DRS VM-host anti-affinity rule violated", alert.Title);
    }

    [Fact]
    public void Must_not_run_on_satisfied_is_silent()
    {
        var rule = VmHostAntiAffine();
        var graph = Graph([rule], (Vm1, HostB));

        Assert.Empty(DrsRuleViolations.Evaluate(graph));
    }

    [Fact]
    public void A_disabled_rule_that_would_be_violated_is_informational_only()
    {
        var rule = AntiAffinity(enabled: false, mandatory: true);
        var graph = Graph([rule], (Vm1, HostA), (Vm2, HostA));

        var alert = Assert.Single(DrsRuleViolations.Evaluate(graph));

        Assert.Equal(AlertSeverity.Info, alert.Severity);
    }

    [Fact]
    public void VCenter_reporting_non_compliance_the_graph_cannot_confirm_is_surfaced_as_informational()
    {
        // Neither VM is currently known to be running anywhere in this graph,
        // so placement alone cannot say why vCenter considers the rule out of
        // compliance — but vCenter's own word for it is still worth passing on.
        var rule = AntiAffinity(mandatory: true, inCompliance: false);
        var graph = new EntityGraph
        {
            Entities = new[] { Cluster(rule) }.ToDictionary(e => e.Id),
        };

        var alert = Assert.Single(DrsRuleViolations.Evaluate(graph));

        Assert.Equal(AlertSeverity.Info, alert.Severity);
        Assert.Equal("DRS rule reported out of compliance", alert.Title);
        Assert.Contains("out of compliance", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void VCenter_agreeing_the_rule_is_satisfied_produces_no_second_alert()
    {
        var rule = AntiAffinity(mandatory: true, inCompliance: true);
        var graph = Graph([rule], (Vm1, HostA), (Vm2, HostB));

        Assert.Empty(DrsRuleViolations.Evaluate(graph));
    }

    [Fact]
    public void Insufficient_placement_data_is_silent_rather_than_guessed_at()
    {
        // Only one of the two VMs the rule names is known to be running
        // anywhere; the rule must not be judged compliant or violated from
        // half the evidence, and vCenter reported no disagreement either.
        var rule = AntiAffinity();
        var graph = Graph([rule], (Vm1, HostA));

        Assert.Empty(DrsRuleViolations.Evaluate(graph));
    }

    [Fact]
    public void A_powered_off_vms_placement_is_not_judged()
    {
        // DRS does not place a machine that is not running. Both VMs land on
        // HostA below -- a violation of this anti-affinity rule, except Vm1
        // is powered off, so only Vm2's placement is actually known and
        // there is nothing left to compare it against.
        var rule = AntiAffinity(mandatory: true);
        var graph = new EntityGraph
        {
            Entities = new[]
            {
                Cluster(rule),
                PoweredOffVm(Vm1),
                Node(Vm2, EntityKind.VirtualMachine),
                Node(HostA, EntityKind.EsxiHost),
            }.ToDictionary(e => e.Id),
            Relationships = [RunsOn(Vm1, HostA), RunsOn(Vm2, HostA)],
        };

        Assert.Empty(DrsRuleViolations.Evaluate(graph));
    }

    [Fact]
    public void A_vm_whose_power_state_was_never_collected_is_still_judged()
    {
        // Silence about power state is not evidence the VM is off -- only an
        // explicit non-"poweredOn" value filters a VM out.
        var rule = AntiAffinity(mandatory: true);
        var graph = Graph([rule], (Vm1, HostA), (Vm2, HostA));

        var alert = Assert.Single(DrsRuleViolations.Evaluate(graph));

        Assert.Equal("DRS anti-affinity rule violated", alert.Title);
    }

    [Fact]
    public void A_cluster_with_no_rules_produces_nothing()
    {
        var graph = new EntityGraph
        {
            Entities = new[] { Cluster() }.ToDictionary(e => e.Id),
        };

        Assert.Empty(DrsRuleViolations.Evaluate(graph));
    }
}
