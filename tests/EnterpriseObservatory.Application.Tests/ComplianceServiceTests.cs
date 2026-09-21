using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using static EnterpriseObservatory.Application.Tests.ComplianceEvaluationTests;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// What people decide about findings: accepting one, and excepting a control.
/// </summary>
/// <remarks>
/// The point of the lifecycle is that it is not an alert's. Nothing here
/// closes by itself; a finding ends when the setting is fixed, and every
/// decision to live with one is attributed and — for an exception — ends.
/// </remarks>
public class ComplianceServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly OperatorIdentity Operator = OperatorIdentity.Verified("ertugrul");

    private static readonly EntityId Host1 = new("vc-1:host-1");

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class Store : IComplianceStore
    {
        private List<ComplianceFinding> _findings = [];
        private readonly List<ComplianceWaiver> _exceptions = [];

        public IReadOnlyList<ComplianceFinding> Findings => _findings;

        public IReadOnlyList<ComplianceWaiver> Exceptions => _exceptions;

        public void Evaluate(
            string catalogueRelease,
            DateTimeOffset nowUtc,
            Func<IReadOnlyList<ComplianceFinding>, IReadOnlyList<ComplianceFinding>> evaluate) =>
            _findings =
            [
                .. _findings.Where(f => f.CatalogueRelease != catalogueRelease),
                .. evaluate([.. _findings.Where(f => f.CatalogueRelease == catalogueRelease)]),
            ];

        public ComplianceFinding? Mutate(
            string catalogueRelease,
            string controlId,
            EntityId entity,
            Func<ComplianceFinding, ComplianceFinding> change)
        {
            var index = _findings.FindIndex(f =>
                f.CatalogueRelease == catalogueRelease && f.ControlId == controlId && f.Entity == entity);

            if (index < 0)
            {
                return null;
            }

            _findings[index] = change(_findings[index]);
            return _findings[index];
        }

        public void AddException(ComplianceWaiver exception) => _exceptions.Add(exception);

        public bool RemoveException(string id, string removedBy, DateTimeOffset removedAtUtc)
        {
            var index = _exceptions.FindIndex(e => e.Id == id && e.RemovedAtUtc is null);

            if (index < 0)
            {
                return false;
            }

            _exceptions[index] = _exceptions[index] with { RemovedBy = removedBy, RemovedAtUtc = removedAtUtc };
            return true;
        }

        /// <summary>Puts a finding of another release beside the evaluated ones.</summary>
        public void Seed(ComplianceFinding finding) => _findings.Add(finding);
    }

    private readonly Clock _clock = new(T0);
    private readonly Store _store = new();
    private readonly ComplianceService _service;

    public ComplianceServiceTests()
    {
        _service = new ComplianceService(Catalogue(LogForwarding), _store, _clock);
    }

    private void Evaluate(string logHost) =>
        _service.Evaluate([Host(settings: ("Syslog.global.logHost", logHost))]);

    private FindingState State() =>
        Assert.Single(_service.Findings()).StateAt(_service.Exceptions(), _clock.UtcNow);

    [Fact]
    public void A_failing_finding_does_not_close_by_itself_between_evaluations()
    {
        Evaluate("");
        _clock.UtcNow = T0.AddDays(30);
        Evaluate("");

        Assert.Equal(FindingState.Failing, State());
    }

    [Fact]
    public void Accepting_a_failing_finding_is_attributed_and_keeps_it_non_compliant()
    {
        Evaluate("");

        var result = _service.Accept("esx-9.log-forwarding", Host1, "CHG-1234", Operator);

        Assert.True(result.Applied);
        Assert.Equal("ertugrul", result.Finding!.Acceptance!.By);
        Assert.Equal("CHG-1234", result.Finding.Acceptance.Reason);
        Assert.Equal(FindingState.Accepted, State());
        Assert.Equal(ComplianceVerdict.Failing, _service.Findings()[0].Verdict);
    }

    [Fact]
    public void An_acceptance_survives_the_next_evaluation()
    {
        Evaluate("");
        _service.Accept("esx-9.log-forwarding", Host1, "", Operator);

        _clock.UtcNow = T0.AddMinutes(5);
        Evaluate("");

        Assert.Equal(FindingState.Accepted, State());
    }

    [Fact]
    public void A_passing_finding_cannot_be_accepted()
    {
        Evaluate("udp://10.0.0.5:514");

        var result = _service.Accept("esx-9.log-forwarding", Host1, "", Operator);

        Assert.False(result.Applied);
        Assert.Equal(ComplianceFailure.NotFailing, result.Failure);
    }

    [Fact]
    public void A_finding_that_does_not_exist_cannot_be_accepted()
    {
        var result = _service.Accept("esx-9.log-forwarding", Host1, "", Operator);

        Assert.Equal(ComplianceFailure.NotFound, result.Failure);
    }

    [Fact]
    public void An_exception_takes_the_finding_out_of_failing_until_it_expires()
    {
        Evaluate("");

        var added = _service.AddException(
            "esx-9.log-forwarding", Host1, "Lab host, logs not retained", "infra-team", T0.AddDays(30), Operator);

        Assert.True(added.Applied);
        Assert.Equal("ertugrul", added.Exception!.CreatedBy);
        Assert.Equal(FindingState.Excepted, State());

        _clock.UtcNow = T0.AddDays(30);

        Assert.Equal(FindingState.Failing, State());
    }

    [Fact]
    public void An_estate_wide_exception_covers_every_host()
    {
        _service.Evaluate(
        [
            Host("vc-1:host-1", settings: ("Syslog.global.logHost", "")),
            Host("vc-1:host-2", settings: ("Syslog.global.logHost", "")),
        ]);

        _service.AddException("esx-9.log-forwarding", null, "Pilot", "owner", T0.AddDays(7), Operator);

        Assert.All(
            _service.Findings(),
            f => Assert.Equal(FindingState.Excepted, f.StateAt(_service.Exceptions(), _clock.UtcNow)));
    }

    [Fact]
    public void An_exception_does_not_cover_a_finding_that_was_not_evaluated()
    {
        _service.Evaluate([Host()]);
        _service.AddException("esx-9.log-forwarding", Host1, "why", "who", T0.AddDays(7), Operator);

        Assert.Equal(FindingState.NotEvaluated, State());
    }

    [Fact]
    public void Removing_an_exception_makes_the_finding_fail_again_at_once()
    {
        Evaluate("");
        var added = _service.AddException("esx-9.log-forwarding", Host1, "why", "who", T0.AddDays(7), Operator);

        Assert.True(_service.RemoveException(added.Exception!.Id, Operator).Applied);
        Assert.Equal(FindingState.Failing, State());
        Assert.Equal(ComplianceFailure.NotFound, _service.RemoveException(added.Exception.Id, Operator).Failure);
    }

    [Fact]
    public void A_removed_exception_stays_on_the_record_with_who_removed_it()
    {
        Evaluate("");
        var added = _service.AddException("esx-9.log-forwarding", Host1, "why", "who", T0.AddDays(7), Operator);

        _clock.UtcNow = T0.AddHours(2);
        var removed = _service.RemoveException(
            added.Exception!.Id, OperatorIdentity.Verified("second-operator"));

        Assert.True(removed.Applied);

        var kept = Assert.Single(_service.Exceptions());

        Assert.Equal("second-operator", kept.RemovedBy);
        Assert.Equal(T0.AddHours(2), kept.RemovedAtUtc);

        // It covered the finding until it was withdrawn, and not after.
        Assert.True(kept.Covers("esx-9.log-forwarding", Host1, T0.AddHours(1)));
        Assert.False(kept.Covers("esx-9.log-forwarding", Host1, T0.AddHours(2)));
    }

    [Fact]
    public void Accepting_an_accepted_finding_is_refused_rather_than_overwriting_it()
    {
        Evaluate("");
        _service.Accept("esx-9.log-forwarding", Host1, "CHG-1", Operator);

        _clock.UtcNow = T0.AddHours(1);
        var second = _service.Accept(
            "esx-9.log-forwarding", Host1, "CHG-2", OperatorIdentity.Verified("second-operator"));

        Assert.False(second.Applied);
        Assert.Equal(ComplianceFailure.AlreadyAccepted, second.Failure);

        var acceptance = _service.Findings()[0].Acceptance!;

        Assert.Equal("ertugrul", acceptance.By);
        Assert.Equal("CHG-1", acceptance.Reason);
        Assert.Equal(T0, acceptance.AtUtc);
    }

    [Fact]
    public void An_acceptance_reason_longer_than_the_record_keeps_is_refused()
    {
        Evaluate("");

        var tooLong = _service.Accept(
            "esx-9.log-forwarding", Host1, new string('x', ComplianceService.MaximumReasonLength + 1), Operator);

        Assert.Equal(ComplianceFailure.TooLong, tooLong.Failure);
        Assert.Null(_service.Findings()[0].Acceptance);

        Assert.True(_service.Accept(
            "esx-9.log-forwarding", Host1, new string('x', ComplianceService.MaximumReasonLength), Operator).Applied);
    }

    [Theory]
    [InlineData(ComplianceService.MaximumReasonLength + 1, 10)]
    [InlineData(10, ComplianceService.MaximumOwnerLength + 1)]
    public void An_exception_reason_or_owner_longer_than_the_record_keeps_is_refused(int reason, int owner)
    {
        var result = _service.AddException(
            "esx-9.log-forwarding", Host1, new string('r', reason), new string('o', owner), T0.AddDays(7), Operator);

        Assert.Equal(ComplianceFailure.TooLong, result.Failure);
        Assert.Empty(_service.Exceptions());
    }

    [Fact]
    public void An_exception_at_the_length_limits_is_recorded()
    {
        var result = _service.AddException(
            "esx-9.log-forwarding",
            Host1,
            new string('r', ComplianceService.MaximumReasonLength),
            new string('o', ComplianceService.MaximumOwnerLength),
            T0.AddDays(7),
            Operator);

        Assert.True(result.Applied);
    }

    [Fact]
    public void An_evaluation_leaves_another_catalogue_releases_findings_alone()
    {
        // A second vendor's catalogue is evaluated by its own service; this
        // one's evaluation must neither see nor replace what that one wrote.
        var other = new ComplianceFinding
        {
            ControlId = "other-vendor.control",
            CatalogueRelease = "other-2026",
            Entity = Host1,
            Verdict = ComplianceVerdict.Failing,
            FirstSeenUtc = T0,
            LastEvaluatedUtc = T0,
        };

        _store.Seed(other);

        Evaluate("");
        Evaluate("udp://10.0.0.5:514");

        Assert.Contains(other, _store.Findings);
        Assert.Equal(2, _store.Findings.Count);
    }

    [Fact]
    public void A_host_whose_source_did_not_report_is_judged_stale_and_dated_when_it_was_read()
    {
        var host = Host(settings: ("Syslog.global.logHost", "")) with { SourceInstanceId = "vc-1" };

        _clock.UtcNow = T0.AddHours(6);
        _service.Evaluate([host], reportingSources: ["vc-2"]);

        var finding = Assert.Single(_service.Findings());

        Assert.True(finding.Stale);
        Assert.Equal(T0, finding.LastEvaluatedUtc);

        _service.Evaluate([host with { LastSeenUtc = T0.AddHours(6) }], reportingSources: ["vc-1"]);

        finding = Assert.Single(_service.Findings());

        Assert.False(finding.Stale);
        Assert.Equal(T0.AddHours(6), finding.LastEvaluatedUtc);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(400)]
    public void An_exception_must_end_in_the_future_and_within_a_year(int days)
    {
        var result = _service.AddException(
            "esx-9.log-forwarding", Host1, "why", "who", T0.AddDays(days), Operator);

        Assert.Equal(ComplianceFailure.BadExpiry, result.Failure);
    }

    [Theory]
    [InlineData("", "owner")]
    [InlineData("reason", " ")]
    public void An_exception_needs_a_reason_and_an_owner(string reason, string owner)
    {
        var result = _service.AddException(
            "esx-9.log-forwarding", Host1, reason, owner, T0.AddDays(7), Operator);

        Assert.Equal(ComplianceFailure.MissingDetail, result.Failure);
    }

    [Fact]
    public void An_exception_to_a_control_not_in_the_catalogue_is_refused()
    {
        var result = _service.AddException("esx-9.made-up", Host1, "why", "who", T0.AddDays(7), Operator);

        Assert.Equal(ComplianceFailure.UnknownControl, result.Failure);
    }

    [Fact]
    public void The_narrower_exception_is_the_one_shown()
    {
        Evaluate("");
        _service.AddException("esx-9.log-forwarding", null, "estate", "a", T0.AddDays(60), Operator);
        var narrow = _service.AddException("esx-9.log-forwarding", Host1, "host", "b", T0.AddDays(7), Operator);

        var covering = _service.Findings()[0].CoveringException(_service.Exceptions(), _clock.UtcNow);

        Assert.Equal(narrow.Exception!.Id, covering!.Id);
    }
}
