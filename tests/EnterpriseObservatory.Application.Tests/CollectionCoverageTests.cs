using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The rule that reports on the product rather than on the estate.
/// </summary>
/// <remarks>
/// Every other rule is entitled to be silent, and this is the only thing that
/// can tell an operator whether a silence was a verdict or a gap. So these
/// tests are mostly about the line between the two: what counts as blindness,
/// and what is merely an estate that has nothing to say.
/// </remarks>
public class CollectionCoverageTests
{
    private static PropertyCoverage Row(
        string property, int asked, int answered, string type = "HostSystem") => new()
        {
            ObjectType = type,
            Property = property,
            Asked = asked,
            Answered = answered,
        };

    private static Dictionary<string, IReadOnlyList<PropertyCoverage>> From(
        params PropertyCoverage[] rows) =>
        new Dictionary<string, IReadOnlyList<PropertyCoverage>>(StringComparer.Ordinal)
        {
            ["vc-1"] = rows,
        };

    [Fact]
    public void A_property_nothing_answered_is_named()
    {
        var alert = Assert.Single(
            CollectionCoverage.Evaluate(From(Row("config.option", asked: 10, answered: 0))));

        Assert.Null(alert.Entity);
        Assert.Contains("config.option", alert.Description, StringComparison.Ordinal);
        Assert.Contains("10 HostSystem", alert.Description, StringComparison.Ordinal);
        Assert.True(alert.IsDerived);
    }

    [Fact]
    public void A_property_everything_answered_is_silent()
    {
        Assert.Empty(CollectionCoverage.Evaluate(
            From(Row("config.option", asked: 10, answered: 10))));
    }

    [Fact]
    public void Partial_coverage_is_not_an_alert()
    {
        // Deliberate. Some objects carry the value, so its absence elsewhere
        // is legible object by object -- and a property that was outright
        // refused is already a CollectionFailure. The gap nothing else
        // catches is the property nobody answered and nobody complained
        // about, which is the only case here.
        Assert.Empty(CollectionCoverage.Evaluate(
            From(Row("config.option", asked: 10, answered: 1))));
    }

    [Fact]
    public void A_type_with_no_objects_is_not_blindness()
    {
        // An estate with no clusters is not an estate whose clusters could
        // not be read. Reporting it would teach an operator that this rule
        // fires for things that are fine.
        Assert.Empty(CollectionCoverage.Evaluate(
            From(Row("configuration.dasConfig.enabled", asked: 0, answered: 0,
                type: "ClusterComputeResource"))));
    }

    [Fact]
    public void Sources_are_judged_apart_rather_than_pooled()
    {
        // Two vCenters are two estates. One answering does not make the
        // other's silence acceptable, and summing them lets a healthy source
        // hide a blind one -- which is the shape of every aggregation bug
        // that ever hid an outage.
        var alerts = CollectionCoverage.Evaluate(
            new Dictionary<string, IReadOnlyList<PropertyCoverage>>(StringComparer.Ordinal)
            {
                ["vc-1"] = [Row("config.option", asked: 10, answered: 10)],
                ["vc-2"] = [Row("config.option", asked: 4, answered: 0)],
            });

        var alert = Assert.Single(alerts);
        Assert.Equal("vc-2", alert.Source);
    }

    [Fact]
    public void Each_blind_property_is_its_own_finding()
    {
        var alerts = CollectionCoverage.Evaluate(From(
            Row("config.option", asked: 10, answered: 0),
            Row("hardware.systemInfo.uuid", asked: 10, answered: 0)));

        Assert.Equal(2, alerts.Count);
        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void The_same_property_on_two_types_is_two_findings()
    {
        // 'name' missing from hosts and 'name' missing from datastores are
        // different problems with different causes, and a fingerprint that
        // dropped the type would let the second resolve the first.
        var alerts = CollectionCoverage.Evaluate(From(
            Row("name", asked: 10, answered: 0),
            Row("name", asked: 41, answered: 0, type: "Datastore")));

        Assert.Equal(2, alerts.Select(a => a.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void The_finding_keeps_its_identity_as_the_counts_move()
    {
        // A property answered by one more object next cycle is the same
        // problem. A fingerprint carrying the counts would retire the
        // operator's clear every time the estate grew by a host.
        var before = Assert.Single(
            CollectionCoverage.Evaluate(From(Row("config.option", asked: 10, answered: 0))));
        var after = Assert.Single(
            CollectionCoverage.Evaluate(From(Row("config.option", asked: 11, answered: 0))));

        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void The_finding_says_the_silence_is_not_health()
    {
        // The entire point. If this sentence goes, the alert becomes a
        // technical notice about a property name rather than a warning that
        // conclusions drawn without it are unreached rather than negative.
        var alert = Assert.Single(
            CollectionCoverage.Evaluate(From(Row("config.option", asked: 10, answered: 0))));

        Assert.Contains("not evidence of health", alert.Description, StringComparison.Ordinal);
        Assert.Contains("unreached rather than negative", alert.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_that_measured_no_coverage_is_silent()
    {
        // Empty coverage is not a claim that coverage was complete, and it is
        // not a claim that it was zero either. A collector that does not
        // measure itself simply says nothing here.
        Assert.Empty(CollectionCoverage.Evaluate(From()));
    }

    [Fact]
    public void A_missing_dictionary_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => CollectionCoverage.Evaluate(null!));
    }
}
