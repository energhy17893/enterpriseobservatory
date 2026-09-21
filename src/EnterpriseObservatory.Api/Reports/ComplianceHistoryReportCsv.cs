using System.Globalization;
using EnterpriseObservatory.Api.Contracts;

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

    public static string Write(IEnumerable<ComplianceReportTransitionRow> rows) =>
        CsvWriter.Write(Header, rows.Select(ToCells).ToList());

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
