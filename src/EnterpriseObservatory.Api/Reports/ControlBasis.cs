namespace EnterpriseObservatory.Api.Reports;

/// <summary>How a control's citation is written in a report: never blank.</summary>
/// <remarks>
/// A control that cites nothing rests on the product's own policy, and says
/// so. A blank cell would read as "not filled in"; the web page uses the same
/// words (see <c>web/src/lib/basis.ts</c>).
/// </remarks>
public static class ControlBasis
{
    public const string NoCitation = "no citation — product policy";

    public static string Label(string? citation) =>
        string.IsNullOrWhiteSpace(citation) ? NoCitation : citation;
}
