using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.BestPracticeControls;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The P3a best-practice rules as <c>eo-bestpractice-1</c> checks: a binary
/// memory-limit fact per virtual machine, and one estate-wide finding per
/// legacy adapter type, never one per virtual machine.
/// </summary>
public class BestPracticeChecksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly ComplianceCatalogue Catalogue = BestPracticeCatalogue.Build(BestPracticeCatalogue.Production);

    private static readonly IReadOnlyDictionary<string, IComplianceCheck> ById =
        BestPracticeCatalogue.ChecksById(BestPracticeCatalogue.Production);

    private static IReadOnlyList<ComplianceFinding> Evaluate(
        IReadOnlyList<Entity> estate,
        IReadOnlyList<ComplianceFinding>? previous = null)
    {
        var graph = EntityGraph.Empty with { Entities = estate.ToDictionary(e => e.Id, e => e) };

        return ComplianceEvaluation.Evaluate(Catalogue, estate, previous ?? [], T0, checksById: ById, graph: graph);
    }

    private static ComplianceFinding One(IReadOnlyList<ComplianceFinding> findings, string control, string subject = "") =>
        Assert.Single(findings, f => f.ControlId == control && f.Subject == subject);

    private static Entity Vm(string id, EntitySizing? sizing = null, params (string Key, string Value)[] settings) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.VirtualMachine,
        DisplayName = id["vc-1:".Length..],
        LastSeenUtc = T0,
        SourceInstanceId = "vc-1",
        Sizing = sizing,
        Settings = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
    };

    private static Entity VCenter(string id = "vc-1:vc-1") => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.VCenter,
        DisplayName = id,
        LastSeenUtc = T0,
        SourceInstanceId = "vc-1",
    };

    // --- catalogue --------------------------------------------------------------

    [Fact]
    public void Production_registers_the_two_p3a_controls_each_with_a_source()
    {
        Assert.Equal([MemoryLimitBelowConfigured, LegacyVirtualAdapters], Catalogue.Controls.Select(c => c.ControlId));
        Assert.All(Catalogue.Controls, c => Assert.False(string.IsNullOrWhiteSpace(c.Source)));
        Assert.Equal("eo-bestpractice-1", Catalogue.Release);
    }

    // --- memory limit -------------------------------------------------------------

    [Fact]
    public void No_limit_passes()
    {
        var vm = Vm("vc-1:vm-1", new EntitySizing { ConfiguredMemoryMb = 4096, MemoryLimitMb = -1 });

        var finding = One(Evaluate([vm]), MemoryLimitBelowConfigured);

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("unlimited", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_limit_below_configured_memory_fails()
    {
        var vm = Vm("vc-1:vm-1", new EntitySizing { ConfiguredMemoryMb = 4096, MemoryLimitMb = 2048 });

        var finding = One(Evaluate([vm]), MemoryLimitBelowConfigured);

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Contains("2048 MB", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("4096 MB", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_limit_at_or_above_configured_memory_passes()
    {
        var vm = Vm("vc-1:vm-1", new EntitySizing { ConfiguredMemoryMb = 4096, MemoryLimitMb = 4096 });

        Assert.Equal(ComplianceVerdict.Passing, One(Evaluate([vm]), MemoryLimitBelowConfigured).Verdict);
    }

    [Fact]
    public void Missing_configured_memory_or_limit_is_not_evaluated()
    {
        var noSizing = Vm("vc-1:vm-1");
        var partial = Vm("vc-1:vm-2", new EntitySizing { ConfiguredMemoryMb = 4096 });

        var findings = Evaluate([noSizing, partial]);

        Assert.Equal(ComplianceVerdict.NotEvaluated, One(findings.Where(f => f.Entity == noSizing.Id).ToList(),
            MemoryLimitBelowConfigured).Verdict);
        Assert.Equal(ComplianceVerdict.NotEvaluated, One(findings.Where(f => f.Entity == partial.Id).ToList(),
            MemoryLimitBelowConfigured).Verdict);
    }

    [Fact]
    public void Fixing_the_limit_passes_on_the_same_row()
    {
        var failing = Evaluate([Vm("vc-1:vm-1", new EntitySizing { ConfiguredMemoryMb = 4096, MemoryLimitMb = 2048 })]);
        var fixedVm = Vm("vc-1:vm-1", new EntitySizing { ConfiguredMemoryMb = 4096, MemoryLimitMb = -1 });

        var finding = One(Evaluate([fixedVm], previous: failing), MemoryLimitBelowConfigured);

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Equal(T0, finding.FirstSeenUtc);
    }

    // --- legacy adapters ----------------------------------------------------------

    private static Entity VmWithAdapters(string id, int e1000 = 0, int e1000e = 0, int lsiLogic = 0) => Vm(id, null,
        (InventoryVerdictKeys.LegacyAdapterE1000, e1000.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        (InventoryVerdictKeys.LegacyAdapterE1000e, e1000e.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        (InventoryVerdictKeys.LegacyAdapterLsiLogic, lsiLogic.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Three_vms_with_one_e1000_each_is_one_finding_with_the_estate_count()
    {
        var estate = new List<Entity>
        {
            VCenter(),
            VmWithAdapters("vc-1:vm-1", e1000: 1),
            VmWithAdapters("vc-1:vm-2", e1000: 1),
            VmWithAdapters("vc-1:vm-3", e1000: 1),
        };

        var findings = Evaluate(estate);

        var e1000Findings = findings.Where(f => f.ControlId == LegacyVirtualAdapters && f.Subject == "E1000").ToList();
        var finding = Assert.Single(e1000Findings);

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Contains("3 adapters", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("vm-1", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void No_legacy_adapters_found_passes_that_type()
    {
        var estate = new List<Entity> { VCenter(), VmWithAdapters("vc-1:vm-1") };

        var finding = One(Evaluate(estate), LegacyVirtualAdapters, "E1000");

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
    }

    [Fact]
    public void Each_adapter_type_gets_its_own_subject_and_finding()
    {
        var estate = new List<Entity>
        {
            VCenter(),
            VmWithAdapters("vc-1:vm-1", e1000e: 2),
            VmWithAdapters("vc-1:vm-2", lsiLogic: 1),
        };

        var findings = Evaluate(estate);

        Assert.Equal(ComplianceVerdict.Passing, One(findings, LegacyVirtualAdapters, "E1000").Verdict);
        Assert.Equal(ComplianceVerdict.Failing, One(findings, LegacyVirtualAdapters, "E1000e").Verdict);
        Assert.Equal(ComplianceVerdict.Failing, One(findings, LegacyVirtualAdapters, "LSI Logic Parallel").Verdict);
    }

    [Fact]
    public void No_vm_devices_read_leaves_every_type_not_evaluated()
    {
        var estate = new List<Entity> { VCenter(), Vm("vc-1:vm-1") };

        var findings = Evaluate(estate);

        Assert.Equal(ComplianceVerdict.NotEvaluated, One(findings, LegacyVirtualAdapters, "E1000").Verdict);
        Assert.Equal(ComplianceVerdict.NotEvaluated, One(findings, LegacyVirtualAdapters, "E1000e").Verdict);
        Assert.Equal(ComplianceVerdict.NotEvaluated, One(findings, LegacyVirtualAdapters, "LSI Logic Parallel").Verdict);
    }

    [Fact]
    public void A_vanished_vm_is_not_counted()
    {
        var gone = VmWithAdapters("vc-1:vm-1", e1000: 1) with { ObservationState = ObservationState.Vanished };
        var estate = new List<Entity> { VCenter(), gone };

        var finding = One(Evaluate(estate), LegacyVirtualAdapters, "E1000");

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
    }
}
