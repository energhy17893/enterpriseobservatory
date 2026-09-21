using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Judging the catalogue against hosts, before anybody decides anything.
/// </summary>
/// <remarks>
/// Most of these are about the two things that must never be confused: a
/// setting nobody configured, which is a verdict, and a setting nobody read,
/// which is not — and about controls this product cannot judge, which are
/// carried with their reason and never counted as passing.
/// </remarks>
public class ComplianceEvaluationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ComplianceControl Control(
        string id, string parameter, string installed = "", string baseline = "", string component = "ESX") => new()
        {
            ControlId = id,
            Component = component,
            Parameter = parameter,
            InstallationDefault = installed,
            BaselineValue = baseline,
        };

    internal static ComplianceCatalogue Catalogue(params ComplianceControl[] controls) => new()
    {
        Release = "910-20260612-01",
        Name = "vcf-9.1",
        Controls = controls,
    };

    internal static readonly ComplianceControl LogForwarding =
        Control("esx-9.log-forwarding", "Syslog.global.logHost", "Undefined", "Site-Specific Log Server");

    internal static Entity Host(
        string id = "vc-1:host-1",
        ObservationState state = ObservationState.Active,
        params (string Key, string Value)[] settings) => new()
        {
            Id = new EntityId(id),
            Kind = EntityKind.EsxiHost,
            DisplayName = id,
            LastSeenUtc = T0,
            ObservationState = state,
            Settings = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>One control against one host read at <paramref name="at"/>, evaluated then.</summary>
    private static ComplianceFinding One(ComplianceControl control, Entity host, IReadOnlyList<ComplianceFinding>? previous = null, DateTimeOffset? at = null) =>
        Assert.Single(ComplianceEvaluation.Evaluate(
            Catalogue(control), [host with { LastSeenUtc = at ?? T0 }], previous ?? [], at ?? T0));

    // --- binding ---------------------------------------------------------------

    [Fact]
    public void A_control_on_a_collected_setting_is_evaluated()
    {
        var bound = Assert.Single(ComplianceEvaluation.Bind(Catalogue(LogForwarding)));

        Assert.True(bound.IsEvaluated);
        Assert.Null(bound.NotEvaluatedReason);
    }

    [Fact]
    public void A_control_on_a_setting_nobody_collects_is_carried_with_its_reason()
    {
        var bound = Assert.Single(ComplianceEvaluation.Bind(Catalogue(
            Control("esx-9.memeagerzero", "Mem.MemEagerZero", "0", "1"))));

        Assert.False(bound.IsEvaluated);
        Assert.Contains("No data collected", bound.NotEvaluatedReason, StringComparison.Ordinal);
        Assert.Contains("Mem.MemEagerZero", bound.NotEvaluatedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_that_is_not_a_single_setting_is_carried_with_its_reason()
    {
        var bound = Assert.Single(ComplianceEvaluation.Bind(Catalogue(
            Control("esx-9.updates", "N/A", "Downlevel", "Current"))));

        Assert.False(bound.IsEvaluated);
        Assert.Contains("not a single setting", bound.NotEvaluatedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Controls_with_no_evaluator_produce_no_per_host_findings()
    {
        // Carried once per control by Bind; repeating "we do not read this" on
        // every host would multiply one fact by the size of the estate.
        var findings = ComplianceEvaluation.Evaluate(
            Catalogue(Control("esx-9.memeagerzero", "Mem.MemEagerZero")),
            [Host(settings: ("Mem.MemEagerZero", "0"))],
            [],
            T0);

        Assert.Empty(findings);
    }

    [Fact]
    public void A_vsphere_8_control_binds_to_the_same_check_by_its_setting()
    {
        var bound = Assert.Single(ComplianceEvaluation.Bind(Catalogue(
            Control("esxi-8.logs-remote", "Syslog.global.logHost", component: "VMware ESXi"))));

        Assert.True(bound.IsEvaluated);
    }

    [Fact]
    public void A_matching_setting_on_a_control_that_is_not_about_hosts_is_not_evaluated()
    {
        var bound = Assert.Single(ComplianceEvaluation.Bind(Catalogue(
            Control("vcenter-9.log-forwarding", "Syslog.global.logHost", component: "vCenter"))));

        Assert.False(bound.IsEvaluated);
        Assert.Contains("hosts only", bound.NotEvaluatedReason, StringComparison.Ordinal);
    }

    // --- verdicts --------------------------------------------------------------

    [Fact]
    public void An_empty_log_target_fails_with_its_evidence()
    {
        var finding = One(LogForwarding, Host(settings: ("Syslog.global.logHost", "")));

        Assert.Equal(ComplianceVerdict.Failing, finding.Verdict);
        Assert.Equal(string.Empty, finding.Observed);
        Assert.Contains("Site-Specific Log Server", finding.Expected, StringComparison.Ordinal);
        Assert.Equal("910-20260612-01", finding.CatalogueRelease);
    }

    [Fact]
    public void A_log_target_passes()
    {
        var finding = One(LogForwarding, Host(settings: ("Syslog.global.logHost", "udp://10.0.0.5:514")));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
    }

    [Fact]
    public void A_setting_the_host_did_not_report_is_not_evaluated_rather_than_failing()
    {
        var finding = One(LogForwarding, Host());

        Assert.Equal(ComplianceVerdict.NotEvaluated, finding.Verdict);
        Assert.Null(finding.Observed);
        Assert.Contains("did not report", finding.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0", ComplianceVerdict.Failing)]      // the shipped value: never times out
    [InlineData("600", ComplianceVerdict.Passing)]
    [InlineData("300", ComplianceVerdict.Passing)]
    [InlineData("601", ComplianceVerdict.Failing)]
    [InlineData("soon", ComplianceVerdict.NotEvaluated)]
    public void A_shell_timeout_must_be_on_and_within_the_baseline(string value, ComplianceVerdict expected)
    {
        var finding = One(
            Control("esx-9.shell-timeout", "UserVars.ESXiShellTimeOut", "0", "600"),
            Host(settings: ("UserVars.ESXiShellTimeOut", value)));

        Assert.Equal(expected, finding.Verdict);
    }

    [Fact]
    public void The_timeout_limit_is_read_from_the_catalogue_not_compiled_in()
    {
        var finding = One(
            Control("esx-9.shell-interactive-timeout", "UserVars.ESXiShellInteractiveTimeOut", "0", "900"),
            Host(settings: ("UserVars.ESXiShellInteractiveTimeOut", "900")));

        Assert.Equal(ComplianceVerdict.Passing, finding.Verdict);
    }

    [Theory]
    [InlineData("true", ComplianceVerdict.Passing)]
    [InlineData("TRUE", ComplianceVerdict.Passing)]
    [InlineData("false", ComplianceVerdict.Failing)]
    public void Audit_records_must_be_enabled(string value, ComplianceVerdict expected)
    {
        var finding = One(
            Control("esx-9.log-audit-local", "Syslog.global.auditRecord.storageEnable", "FALSE", "TRUE"),
            Host(settings: ("Syslog.global.auditRecord.storageEnable", value)));

        Assert.Equal(expected, finding.Verdict);
    }

    [Theory]
    [InlineData("ESX Admins", ComplianceVerdict.Failing)]
    [InlineData("esx admins", ComplianceVerdict.Failing)]
    [InlineData("CORP-vSphere-Admins", ComplianceVerdict.Passing)]
    [InlineData("", ComplianceVerdict.Passing)]
    public void The_admin_group_must_not_be_the_shipped_one(string value, ComplianceVerdict expected)
    {
        var finding = One(
            Control("esx-9.ad-admin-group-name", "Config.HostAgent.plugins.hostsvc.esxAdminsGroup",
                "\"ESX Admins\"", "Site-Specific Active Directory group, or empty"),
            Host(settings: ("Config.HostAgent.plugins.hostsvc.esxAdminsGroup", value)));

        Assert.Equal(expected, finding.Verdict);
    }

    [Fact]
    public void Only_live_hosts_are_judged()
    {
        IReadOnlyList<Entity> estate =
        [
            Host("vc-1:host-1", settings: ("Syslog.global.logHost", "")),
            Host("vc-1:host-2", ObservationState.Vanished, ("Syslog.global.logHost", "")),
            new()
            {
                Id = new EntityId("vc-1:vm-1"),
                Kind = EntityKind.VirtualMachine,
                DisplayName = "vm-1",
                LastSeenUtc = T0,
            },
        ];

        var finding = Assert.Single(ComplianceEvaluation.Evaluate(Catalogue(LogForwarding), estate, [], T0));

        Assert.Equal(new EntityId("vc-1:host-1"), finding.Entity);
    }

    // --- carrying over -----------------------------------------------------------

    [Fact]
    public void Failing_since_survives_while_the_verdict_holds()
    {
        var first = One(LogForwarding, Host(settings: ("Syslog.global.logHost", "")));
        var later = One(LogForwarding, Host(settings: ("Syslog.global.logHost", "")), [first], T0.AddHours(1));

        Assert.Equal(T0, later.FirstSeenUtc);
        Assert.Equal(T0.AddHours(1), later.LastEvaluatedUtc);
    }

    [Fact]
    public void A_change_of_verdict_resets_the_date()
    {
        var failing = One(LogForwarding, Host(settings: ("Syslog.global.logHost", "")));
        var fixedLater = One(
            LogForwarding, Host(settings: ("Syslog.global.logHost", "udp://x:514")), [failing], T0.AddDays(1));

        Assert.Equal(T0.AddDays(1), fixedLater.FirstSeenUtc);
    }

    [Fact]
    public void An_acceptance_survives_while_still_failing_and_ends_when_it_passes()
    {
        var accepted = One(LogForwarding, Host(settings: ("Syslog.global.logHost", ""))) with
        {
            Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0, Reason = "change ticket" },
        };

        var stillFailing = One(
            LogForwarding, Host(settings: ("Syslog.global.logHost", "")), [accepted], T0.AddHours(1));
        Assert.NotNull(stillFailing.Acceptance);

        var passing = One(
            LogForwarding, Host(settings: ("Syslog.global.logHost", "udp://x:514")), [stillFailing], T0.AddHours(2));
        Assert.Null(passing.Acceptance);

        // A regression after the fix is a new failure nobody has seen yet.
        var regressed = One(
            LogForwarding, Host(settings: ("Syslog.global.logHost", "")), [passing], T0.AddHours(3));
        Assert.Null(regressed.Acceptance);
    }

    // --- freshness ----------------------------------------------------------------

    [Fact]
    public void A_finding_is_dated_when_its_host_was_read_not_when_it_was_judged()
    {
        // The graph keeps a silent vCenter's hosts exactly as last read, so
        // judging one a day later re-derives yesterday's verdict. It must say
        // it is yesterday's.
        var host = Host(settings: ("Syslog.global.logHost", "")) with { LastSeenUtc = T0 };

        var finding = Assert.Single(ComplianceEvaluation.Evaluate(
            Catalogue(LogForwarding), [host], [], T0.AddDays(1)));

        Assert.Equal(T0, finding.LastEvaluatedUtc);
        Assert.Equal(T0, finding.FirstSeenUtc);
    }

    [Fact]
    public void A_host_whose_source_did_not_report_this_cycle_is_marked_stale()
    {
        IReadOnlyList<Entity> estate =
        [
            Host("vc-1:host-1", settings: ("Syslog.global.logHost", "")) with { SourceInstanceId = "vc-1" },
            Host("vc-2:host-1", settings: ("Syslog.global.logHost", "")) with { SourceInstanceId = "vc-2" },
        ];

        var findings = ComplianceEvaluation.Evaluate(
            Catalogue(LogForwarding), estate, [], T0, reportingSources: ["vc-1"]);

        Assert.False(findings.Single(f => f.Entity.Value == "vc-1:host-1").Stale);

        // Still judged — an unreachable vCenter does not make its hosts
        // compliant — but not presented as current.
        var silent = findings.Single(f => f.Entity.Value == "vc-2:host-1");

        Assert.True(silent.Stale);
        Assert.Equal(ComplianceVerdict.Failing, silent.Verdict);
    }

    [Fact]
    public void A_stale_finding_keeps_its_first_seen_date_and_acceptance()
    {
        var accepted = One(LogForwarding, Host(settings: ("Syslog.global.logHost", ""))) with
        {
            Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0 },
        };

        var host = Host(settings: ("Syslog.global.logHost", "")) with { SourceInstanceId = "vc-1" };

        var stale = Assert.Single(ComplianceEvaluation.Evaluate(
            Catalogue(LogForwarding), [host], [accepted], T0.AddHours(3), reportingSources: []));

        Assert.True(stale.Stale);
        Assert.Equal(T0, stale.FirstSeenUtc);
        Assert.Equal(T0, stale.LastEvaluatedUtc);
        Assert.NotNull(stale.Acceptance);
    }

    [Fact]
    public void A_reading_dated_in_the_future_is_stamped_no_later_than_now()
    {
        var host = Host(settings: ("Syslog.global.logHost", "")) with { LastSeenUtc = T0.AddMinutes(10) };

        var finding = Assert.Single(ComplianceEvaluation.Evaluate(Catalogue(LogForwarding), [host], [], T0));

        Assert.Equal(T0, finding.LastEvaluatedUtc);
    }

    [Fact]
    public void Findings_from_another_release_are_not_carried_over()
    {
        var old = One(LogForwarding, Host(settings: ("Syslog.global.logHost", ""))) with
        {
            CatalogueRelease = "900-20250101-01",
            Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0 },
        };

        var now = One(LogForwarding, Host(settings: ("Syslog.global.logHost", "")), [old], T0.AddDays(1));

        Assert.Null(now.Acceptance);
        Assert.Equal(T0.AddDays(1), now.FirstSeenUtc);
    }
}
