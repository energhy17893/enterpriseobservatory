using System.Globalization;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Compliance.ContinuityControls;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// M8.8, backup freshness: the backup product's own "last backup" custom
/// attribute against an RPO. Entity = VM, subject <c>''</c>. Every visible VM
/// gets a verdict; no attribute is "not known", never "not backed up".
/// </summary>
public class BackupFreshnessCheckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly ComplianceCatalogue Catalogue = ContinuityCatalogue.Build(ContinuityCatalogue.Production);

    private static readonly IReadOnlyDictionary<string, IComplianceCheck> ById =
        ContinuityCatalogue.ChecksById(ContinuityCatalogue.Production);

    private static IReadOnlyList<ComplianceFinding> Evaluate(
        IReadOnlyList<Entity> estate, IReadOnlyList<ComplianceFinding>? previous = null) =>
        ComplianceEvaluation.Evaluate(
            Catalogue, estate, previous ?? [], T0, checksById: ById,
            graph: EntityGraph.Empty with { Entities = estate.ToDictionary(e => e.Id, e => e) });

    private static ComplianceFinding Of(IReadOnlyList<ComplianceFinding> findings, Entity vm) =>
        Assert.Single(findings, f => f.ControlId == BackupFreshness && f.Entity == vm.Id && f.Subject.Length == 0);

    private static Entity Vm(string id, params (string Key, string Value)[] settings) => new()
    {
        Id = new EntityId(id),
        Kind = EntityKind.VirtualMachine,
        DisplayName = id,
        LastSeenUtc = T0,
        SourceInstanceId = "vc-1",
        Settings = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
    };

    private static Entity BackedUp(string id, DateTimeOffset last) => Vm(id,
        (InventoryVerdictKeys.BackupRead, "true"),
        (InventoryVerdictKeys.BackupField, "Last Backup"),
        (InventoryVerdictKeys.BackupValue, last.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)),
        (InventoryVerdictKeys.BackupLastUtc, last.ToString("o", CultureInfo.InvariantCulture)),
        (InventoryVerdictKeys.BackupTimeBasis, "collector local time, UTC+03:00"));

    [Fact]
    public void The_control_is_registered_with_a_product_policy_source()
    {
        var control = Assert.Single(Catalogue.Controls, c => c.ControlId == "eo-cont.backup-freshness");

        Assert.Equal("Virtual Machine", control.Component);
        Assert.StartsWith("Product policy", control.Source, StringComparison.Ordinal);
        Assert.Equal("Product policy, measured: 10 of 23 gaps 24–30 h (daily schedule), limit = daily + 12 h", control.Source);
        Assert.Equal(TimeSpan.FromHours(36), BackupFreshnessCheck.DefaultRpo);
    }

    [Fact]
    public void A_fresh_backup_passes_and_says_when_and_by_which_attribute()
    {
        var vm = BackedUp("vc-1:vm-1", T0.AddHours(-5));

        var finding = Of(Evaluate([vm]), vm);

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("5 hours ago", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("Last Backup", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("UTC+03:00", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("36 hours", finding.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backup_older_than_the_rpo_fails_with_its_age()
    {
        var hours = BackedUp("vc-1:vm-1", T0.AddHours(-40));
        var days = BackedUp("vc-1:vm-2", T0.AddDays(-40));

        var findings = Evaluate([hours, days]);

        var late = Of(findings, hours);
        Assert.Equal(ComplianceVerdict.Failing, late.Verdict);
        Assert.Contains("40 hours ago", late.Observed, StringComparison.Ordinal);

        var stale = Of(findings, days);
        Assert.Equal(ComplianceVerdict.Failing, stale.Verdict);
        Assert.Contains("40 days ago", stale.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Exactly_the_rpo_passes_and_one_minute_more_fails()
    {
        var at = BackedUp("vc-1:at", T0.AddHours(-36));
        var beyond = BackedUp("vc-1:beyond", T0.AddHours(-36).AddMinutes(-1));

        var findings = Evaluate([at, beyond]);

        Assert.Equal(ComplianceVerdict.Passing, Of(findings, at).Verdict);
        Assert.Equal(ComplianceVerdict.Failing, Of(findings, beyond).Verdict);
    }

    [Fact]
    public void A_daily_backup_that_ran_late_still_passes()
    {
        // Live: 10 of 23 gaps between successive backups were 24-30 h.
        var vm = BackedUp("vc-1:vm-1", T0.AddHours(-30));

        Assert.Equal(ComplianceVerdict.Passing, Of(Evaluate([vm]), vm).Verdict);
    }

    [Fact]
    public void A_vm_with_no_backup_attribute_is_not_evaluated_and_never_called_unprotected()
    {
        var vm = Vm("vc-1:vm-1", (InventoryVerdictKeys.BackupRead, "true"));

        var finding = Of(Evaluate([vm]), vm);

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.StartsWith("no backup attribute", finding.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not backed up", finding.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unparseable_value_is_not_evaluated_and_names_the_value()
    {
        var vm = Vm("vc-1:vm-1",
            (InventoryVerdictKeys.BackupRead, "true"),
            (InventoryVerdictKeys.BackupField, "Last Backup"),
            (InventoryVerdictKeys.BackupValue, "03/04/2026 10:00:00"));

        var finding = Of(Evaluate([vm]), vm);

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("03/04/2026 10:00:00", finding.Reason, StringComparison.Ordinal);
        Assert.Contains("Last Backup", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_attributes_not_read_is_not_evaluated_and_is_not_no_attribute()
    {
        var vm = Vm("vc-1:vm-1");

        var finding = Of(Evaluate([vm]), vm);

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("not read", finding.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("no backup attribute", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backup_time_in_the_future_is_not_evaluated()
    {
        var vm = BackedUp("vc-1:vm-1", T0.AddHours(3));

        var finding = Of(Evaluate([vm]), vm);

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Contains("future", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_backup_passes_on_the_same_row()
    {
        var before = Evaluate([BackedUp("vc-1:vm-1", T0.AddDays(-3))]);
        var vm = BackedUp("vc-1:vm-1", T0.AddHours(-1));

        var finding = Of(Evaluate([vm], before), vm);

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Equal(T0, finding.FirstSeenUtc);
    }

    [Fact]
    public void Every_visible_vm_gets_a_verdict_and_a_vanished_one_leaves()
    {
        var fresh = BackedUp("vc-1:a", T0.AddHours(-1));
        var none = Vm("vc-1:b", (InventoryVerdictKeys.BackupRead, "true"));
        var gone = BackedUp("vc-1:c", T0.AddDays(-9)) with { ObservationState = ObservationState.Vanished };

        var findings = Evaluate([fresh, none, gone]).Where(f => f.ControlId == BackupFreshness).ToList();

        Assert.Equal(2, findings.Count);
        Assert.DoesNotContain(findings, f => f.Entity == gone.Id);
    }

    [Fact]
    public void A_shorter_rpo_can_be_set_for_the_estate()
    {
        var check = new BackupFreshnessCheck(TimeSpan.FromHours(4));
        var control = Catalogue.Controls.Single(c => c.ControlId == BackupFreshness);
        var context = new CheckContext { NowUtc = T0, Graph = EntityGraph.Empty };

        var verdict = Assert.Single(check.Judge(control, BackedUp("vc-1:vm-1", T0.AddHours(-5)), context));

        Assert.Equal(ComplianceVerdict.Failing, verdict.Verdict);
        Assert.Contains("4 hours", verdict.Expected, StringComparison.Ordinal);
    }

    // --- S3: SimpliVity as a second source (ADR-0027 §5) --------------------

    private static Entity WithSimplivity(Entity vm, DateTimeOffset last) => vm with
    {
        Settings = new Dictionary<string, string>(vm.Settings, StringComparer.OrdinalIgnoreCase)
        {
            [InventoryVerdictKeys.SimplivityBackupLastUtc] = last.ToString("o", CultureInfo.InvariantCulture),
        },
    };

    [Fact]
    public void The_newer_of_the_two_sources_is_judged_and_both_are_named()
    {
        // Commvault 30 h ago would pass anyway; SimpliVity 2 h ago is newer.
        var vm = WithSimplivity(BackedUp("vc-1:vm-1", T0.AddHours(-30)), T0.AddHours(-2));

        var finding = Of(Evaluate([vm]), vm);

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("2 hours ago", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("SimpliVity 2 hours ago / attribute 'Last Backup' 30 hours ago", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fresh_simplivity_backup_rescues_a_stale_attribute()
    {
        var vm = WithSimplivity(BackedUp("vc-1:vm-1", T0.AddDays(-5)), T0.AddHours(-3));

        Assert.Equal(ComplianceVerdict.Passing, Of(Evaluate([vm]), vm).Verdict);
    }

    [Fact]
    public void A_fresh_attribute_wins_over_a_stale_simplivity_backup()
    {
        var vm = WithSimplivity(BackedUp("vc-1:vm-1", T0.AddHours(-4)), T0.AddDays(-3));

        var finding = Of(Evaluate([vm]), vm);

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.Contains("4 hours ago (SimpliVity 3 days ago / attribute 'Last Backup' 4 hours ago)", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Simplivity_alone_is_judged_when_the_attributes_were_not_read()
    {
        var stale = WithSimplivity(Vm("vc-1:vm-1"), T0.AddHours(-40));

        var finding = Of(Evaluate([stale]), stale);

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Contains("(SimpliVity 40 hours ago)", finding.Observed, StringComparison.Ordinal);
    }
    // --- a silent annotation namespace (24 Sep 2026) ------------------------

    [Fact]
    public void The_check_declares_it_reads_the_simplivity_namespace()
    {
        Assert.Equal(["simplivity"], new BackupFreshnessCheck().ReadsNamespaces);
    }

    [Fact]
    public void A_silent_simplivity_keeps_the_last_verdict_as_stale_when_the_annotation_is_gone()
    {
        // After a restart the vCenter answered one cycle before SimpliVity:
        // the VM was read fresh, but its SimpliVity annotation was gone
        // (annotations live in memory), and the verdict went NotEvaluated.
        var vm = Vm("vc-1:vm-1");
        var before = Of(Evaluate([WithSimplivity(vm, T0.AddHours(-2))]), vm);
        Assert.Equal(ComplianceVerdict.Passing, before.Verdict);

        var after = Of(
            ComplianceEvaluation.Evaluate(
                Catalogue, [vm], [before], T0.AddMinutes(10), checksById: ById,
                reportingSources: ["vc-1"], silentNamespaces: [SilentSince(T0.AddMinutes(-5))],
                graph: EntityGraph.Empty with { Entities = new Dictionary<EntityId, Entity> { [vm.Id] = vm } }),
            vm);

        Assert.Equal(ComplianceVerdict.Passing, after.Verdict);
        Assert.True(after.Stale);
        Assert.Equal(before.FirstSeenUtc, after.FirstSeenUtc);
        Assert.Equal(before.LastEvaluatedUtc, after.LastEvaluatedUtc);
    }

    [Fact]
    public void A_silent_simplivity_with_no_previous_finding_is_judged_and_marked_stale()
    {
        var vm = Vm("vc-1:vm-1");

        var finding = Of(
            ComplianceEvaluation.Evaluate(
                Catalogue, [vm], [], T0, checksById: ById,
                reportingSources: ["vc-1"], silentNamespaces: [SilentSince(T0.AddMinutes(-5))],
                graph: EntityGraph.Empty with { Entities = new Dictionary<EntityId, Entity> { [vm.Id] = vm } }),
            vm);

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.True(finding.Stale);
    }

    private static SilentNamespace SilentSince(DateTimeOffset since) => new("simplivity", "SimpliVity svt-1", since);

    private static ComplianceFinding AfterSilence(TimeSpan silent)
    {
        var vm = Vm("vc-1:vm-1");
        var before = Of(Evaluate([WithSimplivity(vm, T0.AddHours(-2))]), vm);

        return Of(
            ComplianceEvaluation.Evaluate(
                Catalogue, [vm], [before], T0 + silent, checksById: ById,
                reportingSources: ["vc-1"], silentNamespaces: [SilentSince(T0)],
                graph: EntityGraph.Empty with { Entities = new Dictionary<EntityId, Entity> { [vm.Id] = vm } }),
            vm);
    }

    [Fact]
    public void A_namespace_silent_within_the_annotation_horizon_is_carried_stale()
    {
        var finding = AfterSilence(TimeSpan.FromHours(47));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
        Assert.True(finding.Stale);
    }

    [Fact]
    public void A_namespace_silent_past_the_annotation_horizon_is_not_evaluated_and_says_why()
    {
        // The finding must not outlive the annotation it rests on (ADR-0027's
        // two days): past it the check runs with the annotation absent.
        var finding = AfterSilence(TimeSpan.FromHours(49));

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.StartsWith("SimpliVity svt-1 has not answered for 2 days 1 hour; ", finding.Reason, StringComparison.Ordinal);
        Assert.Contains("last backup time is not known", finding.Reason, StringComparison.Ordinal);
    }
}
