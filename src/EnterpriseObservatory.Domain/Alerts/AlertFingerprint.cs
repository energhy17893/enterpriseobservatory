using System.Globalization;

namespace EnterpriseObservatory.Domain.Alerts;

public enum AlertSeverity
{
    /// <summary>Informational. Never enters the lifecycle — see <see cref="AlertLifecycle"/>.</summary>
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// The stable identity of a problem, as opposed to of a single observation of it.
/// </summary>
/// <remarks>
/// <para>
/// The same fault re-observed on every polling cycle must produce the same
/// fingerprint, otherwise nothing downstream — deduplication, hysteresis,
/// acknowledgement, sticky clear — can work at all.
/// </para>
/// <para>
/// The composition is deliberately coarse: it identifies a <em>problem on a
/// thing</em>, not a moment in time. Nothing time-varying may participate.
/// </para>
/// </remarks>
public readonly record struct AlertFingerprint
{
    private AlertFingerprint(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// Builds a fingerprint from the parts that identify a problem.
    /// </summary>
    /// <param name="source">Which collector or engine raised it.</param>
    /// <param name="title">Short, stable description of the fault.</param>
    /// <param name="category">Grouping such as Hardware, Configuration, Performance.</param>
    /// <param name="objectName">The thing the fault is about.</param>
    /// <param name="checkId">The specific check, when one applies.</param>
    /// <remarks>
    /// Parts are lower-cased and trimmed so that inconsequential differences in
    /// how a vendor spells a name do not split one problem into two alerts.
    /// A literal <c>|</c> in a part is escaped so that it cannot forge a
    /// boundary and collide with a different combination of parts.
    /// </remarks>
    public static AlertFingerprint Create(
        string source,
        string title,
        string category,
        string objectName,
        string? checkId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(objectName);

        var parts = new[] { source, title, category, objectName, checkId ?? string.Empty }
            .Select(Normalize);

        return new AlertFingerprint(string.Join('|', parts));
    }

    private static string Normalize(string part) =>
        part.Trim().ToLowerInvariant().Replace("|", "\\|", StringComparison.Ordinal);

    public override string ToString() => Value;
}

/// <summary>
/// A problem as reported by a collector, rule engine or best-practice check.
/// </summary>
/// <remarks>
/// All four sources produce this same shape so that everything downstream can
/// treat them uniformly. See ADR-0007.
/// </remarks>
public sealed record AlertDefinition
{
    public required AlertFingerprint Fingerprint { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>The entity this is about, when it resolves to one.</summary>
    public EntityId? Entity { get; init; }

    public string Category { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;
}

/// <summary>
/// How many consecutive observations confirm a problem is real.
/// </summary>
/// <remarks>
/// <para>
/// Filters flapping. A warning that appears once and is gone on the next cycle
/// was noise; reporting it costs the operator's attention for nothing.
/// </para>
/// <para>
/// Critical is confirmed immediately: the cost of a one-cycle delay on a real
/// outage is higher than the cost of an occasional spurious critical.
/// </para>
/// </remarks>
public sealed record HysteresisPolicy
{
    public int WarningConsecutiveHits { get; init; } = 2;

    public int CriticalConsecutiveHits { get; init; } = 1;

    public static HysteresisPolicy Default { get; } = new();

    public int RequiredHits(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => CriticalConsecutiveHits,
        AlertSeverity.Warning => WarningConsecutiveHits,
        AlertSeverity.Info => throw new ArgumentOutOfRangeException(
            nameof(severity), severity, "Info alerts do not enter the lifecycle."),
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown severity."),
    };

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"warning={WarningConsecutiveHits}, critical={CriticalConsecutiveHits}");
}
