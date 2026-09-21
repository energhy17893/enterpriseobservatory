using System.Globalization;
using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Api.Reports;

/// <summary>
/// The compliance report's change-history CSV column mapping --
/// <c>/api/reports/compliance.csv?section=history</c>.
/// </summary>
/// <remarks>
/// A second section on the same endpoint rather than a second endpoint
/// (<c>compliance-history.csv</c>): both rows come from the same scope and
/// period filters (<c>control</c>, <c>entity</c>, <c>from</c>, <c>to</c>), so
/// a second endpoint would only duplicate that query-string parsing and the
/// call to <c>ComplianceApi.Report</c> that both already share. One URL with
/// a <c>section</c> switch keeps that parsing and that call written once.
/// </remarks>
public static class ComplianceHistoryReportCsv
{
    private static readonly string[] Header =
    [
        "Control",
        "Entity id",
        "From",
        "To",
        "Observed",
        "At (UTC)",
    ];

    /// <param name="rows">The history rows, oldest first.</param>
    /// <param name="truncated">
    /// Whether more transitions matched the report's scope than the store
    /// returned -- <see cref="ComplianceTransitionsPage.MaxRows"/>.
    /// Appended as a trailing note row rather than silently handing back a
    /// list that looks complete but is a prefix of the period asked for.
    /// </param>
    public static string Write(IEnumerable<ComplianceReportTransitionRow> rows, bool truncated = false)
    {
        var csv = CsvWriter.Write(Header, rows.Select(ToCells).ToList());

        return truncated
            ? csv + CsvWriter.WriteRow(
            [
                "Truncated",
                $"More than {ComplianceTransitionsPage.MaxRows:N0} transitions matched this report's " +
                "scope and period; narrow the control, entity or date range to see the rest.",
            ])
            : csv;
    }

    private static IReadOnlyList<string?> ToCells(ComplianceReportTransitionRow row) =>
    [
        row.ControlId,
        row.EntityId,
        row.From?.ToString(),
        row.To?.ToString(),
        row.Observed,
        row.AtUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    ];
}
