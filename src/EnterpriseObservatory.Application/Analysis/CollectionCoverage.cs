using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// Says which of the things this product reasons about it could not read.
/// </summary>
/// <remarks>
/// <para>
/// Every rule in this folder honours the first principle one entity at a time,
/// by declining a verdict when the evidence is missing. That is correct and it
/// is not enough, because a declined verdict and a clean bill of health look
/// identical from the inbox: both are silence. The estate that reads as
/// healthy because nobody looked at it is the failure this rule exists to make
/// impossible.
/// </para>
/// <para>
/// **Only total blindness is reported.** A property some objects answered is
/// visible without help — the values are there, and their absence elsewhere is
/// legible object by object. A property that was outright refused is already a
/// <see cref="CollectionFailure"/> and is reported through that channel. What
/// is left, and what nothing else catches, is a property that came back from
/// nobody without anyone complaining: no error, no failure, no values, and
/// every rule reading it quietly agreeing that all is well.
/// </para>
/// <para>
/// One finding per property rather than per object. The operator's decision is
/// singular — find out why this cannot be read — and ADR-0021 already measured
/// what happens when a single decision is served as one row per object.
/// </para>
/// </remarks>
public static class CollectionCoverage
{
    /// <summary>Stable across releases: it is part of the fingerprint.</summary>
    public const string RuleId = "collection-coverage";

    private const string Title = "A property this product reasons about could not be read";
    private const string Category = "Collection";
    private const string Platform = "platform";

    /// <summary>
    /// Names every property that nothing answered, although something was asked.
    /// </summary>
    /// <remarks>
    /// Coverage from several sources is judged per source rather than pooled.
    /// Two vCenters are two estates: one answering a property does not mean
    /// the other's silence is fine, and summing them would let a healthy
    /// source hide a blind one.
    /// </remarks>
    public static IReadOnlyList<AlertDefinition> Evaluate(
        IReadOnlyDictionary<string, IReadOnlyList<PropertyCoverage>> bySource)
    {
        ArgumentNullException.ThrowIfNull(bySource);

        var alerts = new List<AlertDefinition>();

        foreach (var (source, coverage) in bySource)
        {
            alerts.AddRange(coverage
                .Where(c => c.IsBlind)
                .Select(c => Blind(source, c)));
        }

        return alerts;
    }

    private static AlertDefinition Blind(string source, PropertyCoverage c) =>
        new()
        {
            // The source is in the fingerprint because the answer differs per
            // vCenter -- a permission granted on one and not the other is the
            // commonest cause -- and the counts are deliberately not, because
            // a property answered by one more object next cycle is the same
            // problem and must not retire the operator's clear.
            Fingerprint = AlertFingerprint.Create(
                Platform, Title, Category,
                $"{source}/{c.ObjectType}/{c.Property}", RuleId),
            Severity = AlertSeverity.Warning,
            Title = Title,
            Description =
                $"'{c.Property}' came back from none of the {c.Asked} " +
                $"{c.ObjectType} object(s) on source '{source}', and nothing reported an " +
                "error. Every rule that reads it is therefore silent, and that silence is " +
                "not evidence of health. The usual cause is the account this product " +
                "connects with lacking the privilege for it; the next is a vSphere version " +
                "that does not carry the property. Until it is resolved, treat any " +
                "conclusion that depends on it as unreached rather than negative.",
            Category = Category,
            Source = source,
            IsDerived = true,
        };
}
