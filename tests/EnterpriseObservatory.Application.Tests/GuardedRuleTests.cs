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
    private static IReadOnlyList<AlertDefinition> Boom() =>
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

    [Fact]
    public void A_rule_that_works_is_passed_through_untouched()
    {
        var findings = new[] { Finding("a"), Finding("b") };

        Assert.Equal(findings, GuardedRule.Run("r", () => findings));
    }

    [Fact]
    public void A_rule_that_throws_does_not_throw()
    {
        // The whole point. Before this, the exception reached the worker, which
        // logged the cycle as failed -- discarding that cycle's collection
        // alerts and notifications along with the rule's own findings.
        Assert.Single(GuardedRule.Run("peer-outliers", Boom));
    }

    [Fact]
    public void The_failure_is_reported_as_an_alert_rather_than_only_logged()
    {
        // ADR-0005: "we are not analysing" belongs in the product, not in a
        // file on the server. An operator who cannot see the blind spot will
        // read the quiet as good news.
        var alert = Assert.Single(GuardedRule.Run("peer-outliers", Boom));

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal("platform", alert.Source);
        Assert.True(alert.IsDerived);
    }

    [Fact]
    public void The_failure_names_the_rule_and_says_what_went_wrong()
    {
        // Without both, the alert says only that something somewhere broke,
        // which tells a support engineer nothing they can act on.
        var alert = Assert.Single(GuardedRule.Run("peer-outliers", Boom));

        Assert.Contains("peer-outliers", alert.Description, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", alert.Description, StringComparison.Ordinal);
        Assert.Contains("Sequence contains no elements", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_failure_admits_that_this_rules_open_alerts_were_resolved_unchecked()
    {
        // The cost of the design, stated in the product rather than hidden in
        // it. A rule that throws reports nothing, and reconciliation reads
        // nothing as "the fault is gone" -- so a real fault closes without
        // anybody rechecking it. The operator has to be told that.
        var alert = Assert.Single(GuardedRule.Run("peer-outliers", Boom));

        Assert.Contains("resolved without being rechecked", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_rules_failing_are_two_alerts_and_not_one()
    {
        // Distinct fingerprints, or fixing one rule would resolve the alert for
        // the other and the second blind spot would vanish from the product
        // while it was still blind.
        var first = Assert.Single(GuardedRule.Run("fault-counters", Boom));
        var second = Assert.Single(GuardedRule.Run("peer-outliers", Boom));

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void The_same_rule_failing_twice_is_one_alert_and_not_two()
    {
        // A rule broken by a bug fails every thirty seconds. A fingerprint that
        // moved would raise two thousand alerts a day for one problem.
        var first = Assert.Single(GuardedRule.Run("peer-outliers", Boom));
        var second = Assert.Single(GuardedRule.Run(
            "peer-outliers", () => throw new ArgumentException("different message")));

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
