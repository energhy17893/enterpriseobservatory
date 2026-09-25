using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>One control of the best-practice catalogue, with its check.</summary>
public sealed record BestPracticeCheck(ComplianceControl Control, IComplianceCheck Check);

/// <summary>
/// The product's own catalogue, <c>eo-bestpractice</c>: rules from the vSphere
/// Performance Best Practices guide and rightsizing KB, judged as findings
/// beside the vendor guide and <c>eo-continuity</c> (P3a; K1/ADR-0024 §1).
/// </summary>
/// <remarks>
/// Same release rule as <see cref="ContinuityCatalogue"/>: adding a control
/// does not bump the release; a control whose meaning changes gets a new id.
/// P3a covers data already collected; P3b (a later PR) adds new collected
/// paths and more controls without a migration.
/// </remarks>
public static class BestPracticeCatalogue
{
    public const string Release = "eo-bestpractice-1";

    public const string Name = "Enterprise Observatory best practice";

    /// <summary>The checks production registers: the P3a and P3b rules, see <see cref="BestPracticeControls"/>.</summary>
    public static IReadOnlyList<BestPracticeCheck> Production => BestPracticeControls.All;

    /// <summary>The catalogue of the given checks' controls, in order.</summary>
    public static ComplianceCatalogue Build(IReadOnlyList<BestPracticeCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        return new ComplianceCatalogue
        {
            Release = Release,
            Name = Name,
            Controls = [.. checks.Select(c => c.Control)],
            BindsById = true,
        };
    }

    /// <summary>The checks by control id, as <see cref="ComplianceEvaluation.Bind"/> binds them.</summary>
    public static IReadOnlyDictionary<string, IComplianceCheck> ChecksById(IReadOnlyList<BestPracticeCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        return checks.ToDictionary(c => c.Control.ControlId, c => c.Check, StringComparer.Ordinal);
    }
}
