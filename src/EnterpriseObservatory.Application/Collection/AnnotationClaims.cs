using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>
/// Two connections annotating the same entities in one namespace: the same
/// federation added twice (ADR-0027).
/// </summary>
/// <remarks>
/// <para>
/// Settled before the graph merge rather than by it. <c>EntityGraph.Merge</c>
/// throws on two writers of one namespace on one entity — right for a
/// programming error, and it stays as that guard — but two OVC addresses of
/// one SimpliVity federation entered as two connections is an operator's
/// configuration, and a throw there would stop the whole inventory cycle,
/// vSphere included.
/// </para>
/// <para>
/// The first source by ordinal name keeps each contested entity; the others'
/// annotations for it are dropped, and one alert per pair names both
/// connections. It resolves once one of them is removed.
/// </para>
/// </remarks>
public static class AnnotationClaims
{
    /// <summary>The producer that speaks for the duplicate-connection alerts.</summary>
    public const string Producer = "annotation-claims";

    public static (IReadOnlyList<EntityAnnotation> Kept, IReadOnlyList<AlertDefinition> Alerts) Settle(
        IReadOnlyList<EntityAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);

        var owner = new Dictionary<(EntityId, string), string>();
        var kept = new List<EntityAnnotation>();
        var pairs = new HashSet<(string Namespace, string First, string Second)>();

        foreach (var annotation in annotations.OrderBy(a => a.SourceInstanceId, StringComparer.Ordinal))
        {
            var key = (annotation.Entity, annotation.Namespace);

            if (!owner.TryGetValue(key, out var first))
            {
                owner[key] = annotation.SourceInstanceId;
                kept.Add(annotation);
            }
            else if (!string.Equals(first, annotation.SourceInstanceId, StringComparison.Ordinal))
            {
                pairs.Add((annotation.Namespace, first, annotation.SourceInstanceId));
            }
            else
            {
                // One source twice on one entity is a collector defect, not a
                // configuration: left for Merge's guard to throw on.
                kept.Add(annotation);
            }
        }

        return (kept, [.. pairs.Select(Alert)]);
    }

    private static AlertDefinition Alert((string Namespace, string First, string Second) pair)
    {
        var what = string.Equals(pair.Namespace, "simplivity", StringComparison.OrdinalIgnoreCase)
            ? "SimpliVity federation"
            : $"'{pair.Namespace}' platform";
        var title = $"Connections {pair.First} and {pair.Second} report the same {what}";

        return new AlertDefinition
        {
            Fingerprint = AlertFingerprint.Create(
                Producer, "Duplicate connection", "Configuration", $"{pair.Namespace}:{pair.First}:{pair.Second}"),
            Severity = AlertSeverity.Warning,
            Title = title,
            Description =
                $"'{pair.First}' and '{pair.Second}' annotate the same entities under '{pair.Namespace}.*', " +
                $"so they read one {what} twice. '{pair.Second}' is ignored where they overlap; remove one " +
                "of the two connections (ADR-0027).",
            Category = "Configuration",
            Source = Producer,
        };
    }
}
