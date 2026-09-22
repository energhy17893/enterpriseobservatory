using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// What a broken rule is allowed to cost.
/// </summary>
/// <remarks>
/// The answer is: itself, and nothing else. Collection sources and storage
/// have been isolated since the beginning; rules were the last part of the
/// cycle that could take the whole thing down, and a bug in a peer comparison
/// stopping the product from saying a vCenter was unreachable is the failure
/// these tests exist to prevent.
/// </remarks>
public class GuardedRuleTests
{
    private static IReadOnlyList<SubjectVerdict> Boom() =>
        throw new InvalidOperationException("Sequence contains no elements");

    private static AlertDefinition Finding(string id) => new()
    {
        Fingerprint = AlertFingerprint.Create("vc-1", id, "Fault", id, id),
        Severity = AlertSeverity.Warning,
        Title = id,
        Description = id,
        Category = "Fault",
        Source = "vc-1",
    };

    private static ConditionPresent Present(AlertDefinition alert) => new()
    {
        Covers = [alert.Fingerprint],
        Alerts = [alert],
        EvidenceAtUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void A_rule_that_works_is_passed_through_untouched()
    {
        IReadOnlyList<SubjectVerdict> findings = [Present(Finding("a")), Present(Finding("b"))];

        var result = GuardedRule.Run("r", () => findings);

        Assert.Same(findings, result.Verdicts);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void A_rule_that_throws_does_not_throw()
    {
        // The whole point. Before this, the exception reached the worker, which
        // logged the cycle as failed -- discarding that cycle's collection
        // alerts and notifications along with the rule's own findings.
        Assert.Single(GuardedRule.Run("peer-outliers", Boom).Failures);
    }

    [Fact]
    public void The_failure_is_reported_as_an_alert_rather_than_only_logged()
    {
        // ADR-0005: "we are not analysing" belongs in the product, not in a
        // file on the server. An operator who cannot see the blind spot will
        // read the quiet as good news.
        var alert = Assert.Single(GuardedRule.Run("peer-outliers", Boom).Failures);

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal("platform", alert.Source);
        Assert.True(alert.IsDerived);
    }

    [Fact]
    public void The_failure_names_the_rule_and_says_what_went_wrong()
    {
        // Without both, the alert says only that something somewhere broke,
        // which tells a support engineer nothing they can act on.
        var alert = Assert.Single(GuardedRule.Run("peer-outliers", Boom).Failures);

        Assert.Contains("peer-outliers", alert.Description, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", alert.Description, StringComparison.Ordinal);
        Assert.Contains("Sequence contains no elements", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_failure_says_this_rules_open_alerts_are_kept_open_and_stale()
    {
        // Until ADR-0026 this said they were resolved without being rechecked:
        // the cost of a two-valued design, stated in the product. Now they stay.
        var alert = Assert.Single(GuardedRule.Run("peer-outliers", Boom).Failures);

        Assert.Contains("kept open and marked stale", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_that_throws_gives_every_alert_it_holds_rule_failed_and_nothing_else()
    {
        var held = new HeldAlert(Finding("a").Fingerprint, new EntityId("esx01"));

        var result = GuardedRule.Run("peer-outliers", Boom, [held]);

        var unknown = Assert.IsType<Unknown>(Assert.Single(result.Verdicts));
        Assert.Equal(UnknownReason.RuleFailed, unknown.Reason);
        Assert.Equal([held.Fingerprint], unknown.Covers);
        Assert.Contains("InvalidOperationException", unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_rules_failing_are_two_alerts_and_not_one()
    {
        // Distinct fingerprints, or fixing one rule would resolve the alert for
        // the other and the second blind spot would vanish from the product
        // while it was still blind.
        var first = Assert.Single(GuardedRule.Run("fault-counters", Boom).Failures);
        var second = Assert.Single(GuardedRule.Run("peer-outliers", Boom).Failures);

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void The_same_rule_failing_twice_is_one_alert_and_not_two()
    {
        // A rule broken by a bug fails every thirty seconds. A fingerprint that
        // moved would raise two thousand alerts a day for one problem.
        var first = Assert.Single(GuardedRule.Run("peer-outliers", Boom).Failures);
        var second = Assert.Single(GuardedRule.Run(
            "peer-outliers", () => throw new ArgumentException("different message")).Failures);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void A_cancelled_cycle_is_not_reported_as_a_broken_rule()
    {
        // Shutdown is not a defect, and an alert raised on every restart would
        // teach operators that this alert means nothing.
        Assert.Throws<OperationCanceledException>(
            () => GuardedRule.Run("peer-outliers", () => throw new OperationCanceledException()));
    }
}
