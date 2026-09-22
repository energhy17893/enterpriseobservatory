using EnterpriseObservatory.Application.Compliance;

namespace EnterpriseObservatory.Api;

/// <summary>
/// The catalogue a control comes from, as the screens name it (K3 note §1.1).
/// </summary>
/// <remarks>
/// Two values on this build: Broadcom's guide and the product's own
/// continuity catalogue. The vendor guide must never read as "ours", so the
/// source travels with every control rather than being inferred from its id.
/// Not the citation: what a control's expectation rests on is
/// <c>ComplianceControl.Source</c>, labelled "basis:" on screen.
/// </remarks>
public static class ComplianceSources
{
    public const string Scg = "Broadcom SCG";

    public const string Continuity = "eo-continuity";

    /// <summary>The source of a catalogue, by its name: the product's own, or else the vendor guide.</summary>
    public static string Of(string catalogueName) =>
        string.Equals(catalogueName, ContinuityCatalogue.Name, StringComparison.Ordinal) ? Continuity : Scg;
}
