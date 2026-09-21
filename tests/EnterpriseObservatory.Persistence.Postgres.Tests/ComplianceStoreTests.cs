using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// Compliance findings and exceptions, against a real server.
/// </summary>
/// <remarks>
/// The claims are the ones a restart would break: that an acceptance and its
/// author come back, that a null observation stays null rather than becoming
/// an empty string (the difference between "not read" and "not configured"),
/// and that an evaluation replaces the whole set rather than leaving findings
/// for a host that has gone.
/// </remarks>
public class ComplianceStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

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
            Reason = observed is null ? "The host did not report it." : null,
            FirstSeenUtc = T0,
            LastEvaluatedUtc = T0.AddMinutes(5),
        };

    [SkippableFact]
    public void Findings_and_their_acceptance_survive_a_restart()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate(_ => [Finding(), Finding("vc-1:host-2", ComplianceVerdict.NotEvaluated, null)]);
        store.Mutate("esxi-8.logs-remote", new EntityId("vc-1:host-1"), f => f with
        {
            Acceptance = new FindingAcceptance { By = "ertugrul", AtUtc = T0.AddHours(1), Reason = "CHG-1" },
        });

        _live.Restart();

        var findings = new PostgresComplianceStore(_live.Database).Findings
            .OrderBy(f => f.Entity.Value, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal(ComplianceVerdict.Failing, findings[0].Verdict);
        Assert.Equal(string.Empty, findings[0].Observed);
        Assert.Equal("ertugrul", findings[0].Acceptance!.By);
        Assert.Equal(T0.AddHours(1), findings[0].Acceptance!.AtUtc);
        Assert.Equal(T0, findings[0].FirstSeenUtc);

        Assert.Equal(ComplianceVerdict.NotEvaluated, findings[1].Verdict);
        Assert.Null(findings[1].Observed);
        Assert.Null(findings[1].Acceptance);
    }

    [SkippableFact]
    public void An_evaluation_replaces_every_finding()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.Evaluate(_ => [Finding("vc-1:host-1"), Finding("vc-1:host-2")]);
        store.Evaluate(previous => [.. previous.Where(f => f.Entity.Value == "vc-1:host-1")]);

        _live.Restart();

        Assert.Equal(
            "vc-1:host-1",
            Assert.Single(new PostgresComplianceStore(_live.Database).Findings).Entity.Value);
    }

    [SkippableFact]
    public void An_exception_survives_a_restart_and_can_be_removed()
    {
        RequireDatabase();

        var store = new PostgresComplianceStore(_live.Database);
        store.AddException(new ComplianceWaiver
        {
            Id = "x1",
            ControlId = "esxi-8.logs-remote",
            Entity = null,
            Reason = "Lab",
            Owner = "infra",
            CreatedBy = "ertugrul",
            CreatedAtUtc = T0,
            ExpiresUtc = T0.AddDays(30),
        });

        _live.Restart();

        var reopened = new PostgresComplianceStore(_live.Database);
        var exception = Assert.Single(reopened.Exceptions);

        Assert.Null(exception.Entity);
        Assert.Equal(T0.AddDays(30), exception.ExpiresUtc);
        Assert.True(reopened.RemoveException("x1"));
        Assert.False(reopened.RemoveException("x1"));

        _live.Restart();

        Assert.Empty(new PostgresComplianceStore(_live.Database).Exceptions);
    }
}
