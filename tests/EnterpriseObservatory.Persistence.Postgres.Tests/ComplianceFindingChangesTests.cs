using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// How an evaluation's result is sorted into writes, without a server.
/// </summary>
/// <remarks>
/// This sorting is what decides how many rows a cycle costs. The review
/// measured the old store at every finding deleted and re-inserted every
/// cycle; the claim here is that a steady estate costs one timestamp update
/// per finding and no history, and that everything else is still written.
/// </remarks>
public class ComplianceFindingChangesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ComplianceFinding Finding(
        string entity = "vc-1:host-1",
        ComplianceVerdict verdict = ComplianceVerdict.Failing,
        string? observed = "") => new()
        {
            ControlId = "esxi-8.logs-remote",
            CatalogueRelease = "803-20260612-01",
            Entity = new EntityId(entity),
            EntityName = "esx-01",
            Verdict = verdict,
            Observed = observed,
            Expected = "set to a log target",
            FirstSeenUtc = T0,
            LastEvaluatedUtc = T0,
        };

    [Fact]
    public void A_steady_estate_only_moves_the_evidence_time()
    {
        IReadOnlyList<ComplianceFinding> before = [.. Enumerable.Range(1, 120).Select(i => Finding($"h-{i}"))];
        IReadOnlyList<ComplianceFinding> after = [.. before.Select(f => f with { LastEvaluatedUtc = T0.AddMinutes(5) })];

        var changes = ComplianceFindingChanges.Between(before, after, T0.AddMinutes(5));

        Assert.Equal(120, changes.Touched.Count);
        Assert.Empty(changes.Written);
        Assert.Empty(changes.Removed);
        Assert.Empty(changes.Transitions);
    }

    [Fact]
    public void A_finding_whose_evidence_time_did_not_move_is_not_written_at_all()
    {
        // A silent vCenter's hosts: judged again, same reading, same date.
        IReadOnlyList<ComplianceFinding> before = [Finding() with { Stale = true }];

        var changes = ComplianceFindingChanges.Between(before, before, T0.AddMinutes(5));

        Assert.Empty(changes.Touched);
        Assert.Empty(changes.Written);
    }

    [Fact]
    public void Changed_evidence_is_written_without_a_transition_while_the_verdict_holds()
    {
        var changes = ComplianceFindingChanges.Between(
            [Finding(observed: "")],
            [Finding(observed: "   ")],
            T0);

        Assert.Single(changes.Written);
        Assert.Empty(changes.Transitions);
    }

    [Fact]
    public void Going_stale_or_being_accepted_is_a_write()
    {
        var changes = ComplianceFindingChanges.Between(
            [Finding("h-1"), Finding("h-2")],
            [
                Finding("h-1") with { Stale = true },
                Finding("h-2") with { Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0 } },
            ],
            T0);

        Assert.Equal(2, changes.Written.Count);
        Assert.Empty(changes.Touched);
    }

    [Fact]
    public void New_changed_and_departed_findings_each_leave_a_transition()
    {
        var changes = ComplianceFindingChanges.Between(
            [Finding("stays"), Finding("fixed"), Finding("gone", ComplianceVerdict.Passing)],
            [
                Finding("stays"),
                Finding("fixed", ComplianceVerdict.Passing, "udp://x:514") with { LastEvaluatedUtc = T0.AddHours(1) },
                Finding("new", ComplianceVerdict.NotEvaluated, null),
            ],
            T0.AddHours(1));

        Assert.Equal(["fixed", "new"], changes.Written.Select(f => f.Entity.Value).Order());
        Assert.Equal("gone", Assert.Single(changes.Removed).Entity.Value);

        var byEntity = changes.Transitions.ToDictionary(t => t.Entity.Value);

        Assert.Equal(3, byEntity.Count);
        Assert.Equal(ComplianceVerdict.Failing, byEntity["fixed"].From);
        Assert.Equal(ComplianceVerdict.Passing, byEntity["fixed"].To);
        Assert.Equal("udp://x:514", byEntity["fixed"].Observed);
        Assert.Equal(T0.AddHours(1), byEntity["fixed"].EvidenceUtc);
        Assert.Null(byEntity["new"].From);
        Assert.Equal(ComplianceVerdict.NotEvaluated, byEntity["new"].To);
        Assert.Equal(ComplianceVerdict.Passing, byEntity["gone"].From);
        Assert.Null(byEntity["gone"].To);
        Assert.All(changes.Transitions, t => Assert.Equal(T0.AddHours(1), t.AtUtc));
    }
}
