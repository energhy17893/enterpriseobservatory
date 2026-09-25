using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>One control of the SimpliVity catalogue, with its check.</summary>
public sealed record SimplivityCheck(ComplianceControl Control, IComplianceCheck Check);

/// <summary>
/// The product's own catalogue, <c>eo-simplivity</c>: HPE's cross-environment
/// rules for SimpliVity (reference-approaches §10.8), judged as findings
/// beside the vendor guide, <c>eo-continuity</c> and <c>eo-bestpractice</c>.
/// </summary>
/// <remarks>
/// Same release rule as <see cref="ContinuityCatalogue"/>: adding a control
/// does not bump the release; a control whose meaning changes gets a new id.
/// Every control judges only entities that carry a <c>simplivity.*</c>
/// annotation (ADR-0027); anything else is out of scope and gets no finding
/// at all, not NotEvaluated. S2a covers data collected today; S2b adds the
/// host-side rules (OVC reservation and pool, lockdown exception, storage
/// MTU, DRS must groups).
/// </remarks>
public static class SimplivityCatalogue
{
    public const string Release = "eo-simplivity-1";

    public const string Name = "Enterprise Observatory SimpliVity";

    /// <summary>The checks production registers, see <see cref="SimplivityControls"/>.</summary>
    public static IReadOnlyList<SimplivityCheck> Production => SimplivityControls.All;

    /// <summary>The catalogue of the given checks' controls, in order.</summary>
    public static ComplianceCatalogue Build(IReadOnlyList<SimplivityCheck> checks)
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
    public static IReadOnlyDictionary<string, IComplianceCheck> ChecksById(IReadOnlyList<SimplivityCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        return checks.ToDictionary(c => c.Control.ControlId, c => c.Check, StringComparer.Ordinal);
    }
}
