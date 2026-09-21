using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// The subject in a finding's identity (K1, migration 11): sorted into writes
/// without a server, then stored and read back against one.
/// </summary>
/// <remarks>
/// The two events that must not look alike: a subject that is fixed stays as
/// a passing row with a <c>Failing → Passing</c> transition; a subject that
/// is gone leaves a <c>to_verdict NULL</c> transition carrying who accepted
/// it and why, its last observation and its label.
/// </remarks>
public class ComplianceSubjectTests : IDisposable
{
    private const string Release = "eo-continuity-1";

    private const string Control = "eo-cont.drs-rule";

    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly EntityId Cluster = new("vc-1:domain-c1");

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private static ComplianceFinding Finding(
        string subject,
        ComplianceVerdict verdict = ComplianceVerdict.Failing,
        string? label = null,
        string? observed = "violated") => new()
        {
            ControlId = Control,
            CatalogueRelease = Release,
            Entity = Cluster,
            EntityName = "cluster-1",
            Subject = subject,
            SubjectLabel = label ?? $"rule {subject}",
            Verdict = verdict,
            Observed = observed,
            Expected = "rule satisfied",
            FirstSeenUtc = T0,
            LastEvaluatedUtc = T0,
        };

    private static readonly FindingAcceptance Accepted = new() { By = "ertugrul", AtUtc = T0, Reason = "CHG-42" };

    // --- sorting, without a server ------------------------------------------------

    [Fact]
    public void Two_subjects_of_one_control_and_entity_are_two_findings()
    {
        var changes = ComplianceFindingChanges.Between([], [Finding("uuid-1"), Finding("uuid-2")], T0);

        Assert.Equal(2, changes.Written.Count);
        Assert.Equal(["uuid-1", "uuid-2"], changes.Transitions.Select(t => t.Subject).Order());
    }

    [Fact]
    public void A_fixed_subject_is_a_transition_to_passing_and_the_row_is_kept()
    {
        var changes = ComplianceFindingChanges.Between(
            [Finding("uuid-1"), Finding("uuid-2")],
            [Finding("uuid-1", ComplianceVerdict.Passing, observed: "ok"), Finding("uuid-2")],
            T0.AddHours(1));

        Assert.Empty(changes.Removed);
        Assert.Equal("uuid-1", Assert.Single(changes.Written).Subject);

        var transition = Assert.Single(changes.Transitions);

        Assert.Equal("uuid-1", transition.Subject);
        Assert.Equal(ComplianceVerdict.Failing, transition.From);
        Assert.Equal(ComplianceVerdict.Passing, transition.To);
    }

    [Fact]
    public void A_vanished_subject_leaves_a_null_transition_carrying_its_acceptance()
    {
        var changes = ComplianceFindingChanges.Between(
            [Finding("uuid-1", label: "keep-apart") with { Acceptance = Accepted }, Finding("uuid-2")],
            [Finding("uuid-2")],
            T0.AddHours(1));

        Assert.Equal("uuid-1", Assert.Single(changes.Removed).Subject);

        var gone = Assert.Single(changes.Transitions);

        Assert.Equal("uuid-1", gone.Subject);
        Assert.Equal("keep-apart", gone.SubjectLabel);
        Assert.Equal(ComplianceVerdict.Failing, gone.From);
        Assert.Null(gone.To);
        Assert.Equal("violated", gone.Observed);
        Assert.Equal("ertugrul", gone.AcceptedBy);
        Assert.Equal("CHG-42", gone.AcceptedReason);
    }

    [Fact]
    public void A_renamed_subject_is_rewritten_without_a_transition()
    {
        var changes = ComplianceFindingChanges.Between(
            [Finding("uuid-1", label: "old") with { Acceptance = Accepted }],
            [Finding("uuid-1", label: "new") with { Acceptance = Accepted }],
            T0.AddHours(1));

        Assert.Equal("new", Assert.Single(changes.Written).SubjectLabel);
        Assert.Empty(changes.Removed);
        Assert.Empty(changes.Transitions);
    }

    // --- against a server ---------------------------------------------------------------

    [SkippableFact]
    public void Subjects_and_labels_survive_a_restart_as_separate_findings()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate(Release, T0, _ => [Finding("uuid-1", label: "a"), Finding("uuid-2", ComplianceVerdict.Passing, "b")]);

        _live.Restart();

        var findings = new PostgresComplianceStore(_live.Database).Findings.OrderBy(f => f.Subject).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal(("uuid-1", "a", ComplianceVerdict.Failing), (findings[0].Subject, findings[0].SubjectLabel, findings[0].Verdict));
        Assert.Equal(("uuid-2", "b", ComplianceVerdict.Passing), (findings[1].Subject, findings[1].SubjectLabel, findings[1].Verdict));
    }

    [SkippableFact]
    public void An_acceptance_lands_on_its_own_subject_only()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate(Release, T0, _ => [Finding("uuid-1"), Finding("uuid-2")]);

        var changed = store.Mutate(Release, Control, Cluster, "uuid-2", f => f with { Acceptance = Accepted });

        Assert.Equal("uuid-2", changed!.Subject);

        _live.Restart();

        var findings = new PostgresComplianceStore(_live.Database).Findings;

        Assert.Null(findings.Single(f => f.Subject == "uuid-1").Acceptance);
        Assert.Equal("CHG-42", findings.Single(f => f.Subject == "uuid-2").Acceptance!.Reason);
    }

    [SkippableFact]
    public void A_fixed_subject_and_a_vanished_one_are_recorded_differently()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate(Release, T0, _ => [Finding("fixed"), Finding("gone", label: "keep-apart")]);
        store.Mutate(Release, Control, Cluster, "gone", f => f with { Acceptance = Accepted });

        // Unchanged rows are touched by subject, not by control and entity.
        store.Evaluate(Release, T0.AddMinutes(5), previous =>
            [.. previous.Select(f => f with { LastEvaluatedUtc = T0.AddMinutes(5) })]);

        store.Evaluate(Release, T0.AddHours(1), _ =>
            [Finding("fixed", ComplianceVerdict.Passing, observed: "ok") with { FirstSeenUtc = T0.AddHours(1) }]);

        _live.Restart();

        var reopened = new PostgresComplianceStore(_live.Database);

        var row = Assert.Single(reopened.Findings);
        Assert.Equal(("fixed", ComplianceVerdict.Passing), (row.Subject, row.Verdict));

        var fixedHistory = reopened.Transitions(Release, Control, Cluster, "fixed");
        Assert.Equal([null, ComplianceVerdict.Failing], fixedHistory.Select(t => t.From));
        Assert.Equal(ComplianceVerdict.Passing, fixedHistory[^1].To);

        var goneHistory = reopened.Transitions(Release, Control, Cluster, "gone");
        var gone = goneHistory[^1];

        Assert.Null(gone.To);
        Assert.Equal("gone", gone.Subject);
        Assert.Equal("keep-apart", gone.SubjectLabel);
        Assert.Equal("violated", gone.Observed);
        Assert.Equal("ertugrul", gone.AcceptedBy);
        Assert.Equal("CHG-42", gone.AcceptedReason);

        var since = reopened.TransitionsSince(T0.AddDays(-1), catalogueRelease: Release);
        Assert.Contains(since.Transitions, t => t.Subject == "gone" && t.To is null && t.AcceptedBy == "ertugrul");
    }

    [SkippableFact]
    public void A_renamed_subject_keeps_its_acceptance_in_the_store()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate(Release, T0, _ => [Finding("uuid-1", label: "old") with { Acceptance = Accepted }]);
        store.Evaluate(Release, T0.AddHours(1), _ => [Finding("uuid-1", label: "new") with { Acceptance = Accepted }]);

        _live.Restart();

        var finding = Assert.Single(new PostgresComplianceStore(_live.Database).Findings);

        Assert.Equal("new", finding.SubjectLabel);
        Assert.Equal("ertugrul", finding.Acceptance!.By);
    }

    [SkippableFact]
    public void An_exception_keeps_its_subject_and_null_means_every_subject()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);

        ComplianceWaiver Waiver(string id, string? subject) => new()
        {
            Id = id,
            ControlId = Control,
            Entity = Cluster,
            Subject = subject,
            Reason = "r",
            Owner = "o",
            CreatedBy = "c",
            CreatedAtUtc = T0,
            ExpiresUtc = T0.AddDays(30),
        };

        store.AddException(Waiver("one", "uuid-1"));
        store.AddException(Waiver("all", null));

        _live.Restart();

        var exceptions = new PostgresComplianceStore(_live.Database).Exceptions.ToDictionary(e => e.Id);

        Assert.Equal("uuid-1", exceptions["one"].Subject);
        Assert.Null(exceptions["all"].Subject);
    }

    [SkippableFact]
    public void Existing_findings_get_the_empty_subject()
    {
        RequireDatabase();

        // What an SCG row written before migration 11 reads back as.
        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate("803-20260612-01", T0, _ =>
        [
            new ComplianceFinding
            {
                ControlId = "esxi-8.logs-remote",
                CatalogueRelease = "803-20260612-01",
                Entity = new EntityId("vc-1:host-1"),
                Verdict = ComplianceVerdict.Failing,
                FirstSeenUtc = T0,
                LastEvaluatedUtc = T0,
            },
        ]);

        _live.Restart();

        var finding = Assert.Single(new PostgresComplianceStore(_live.Database).Findings);

        Assert.Equal(string.Empty, finding.Subject);
        Assert.Null(finding.SubjectLabel);
    }
}
