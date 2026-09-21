using System.Globalization;
using EnterpriseObservatory.Api.Contracts;

namespace EnterpriseObservatory.Api.Reports;

/// <summary>The compliance report's findings-detail CSV column mapping.</summary>
/// <remarks>
/// The default section of <c>/api/reports/compliance.csv</c>; see
/// <see cref="ComplianceHistoryReportCsv"/> for the <c>section=history</c>
/// one. Like <see cref="AlertsReportCsv"/>, the only report-specific code
/// here is the header list and the row mapping -- everything else is
/// <see cref="CsvWriter"/>.
/// </remarks>
public static class ComplianceFindingsReportCsv
{
    private static readonly string[] Header =
    [
        "Control",
        "Title",
        "Priority",
        "Entity",
        "Entity id",
        "State",
        "Not evaluated reason",
        "Observed",
        "Expected",
        "First seen (UTC)",
        "Last evaluated (UTC)",
        "Stale",
        "Accepted by",
        "Accepted at (UTC)",
        "Accepted reason",
        "Exception owner",
        "Exception reason",
        "Exception created by",
        "Exception created at (UTC)",
        "Exception expires (UTC)",

        // Added with K1, after the columns an existing import already reads.
        "Subject",
        "Subject label",
    ];

    public static string Write(IEnumerable<ComplianceReportFindingRow> rows) =>
        CsvWriter.Write(Header, rows.Select(ToCells).ToList());

    private static IReadOnlyList<string?> ToCells(ComplianceReportFindingRow row) =>
    [
        row.ControlId,
        row.ControlTitle,
        row.Priority,
        row.EntityName,
        row.EntityId,
        row.State.ToString(),
        row.NotEvaluatedReason,
        row.Observed,
        row.Expected,
        Iso(row.FirstSeenUtc),
        Iso(row.LastEvaluatedUtc),
        row.Stale ? "yes" : "no",
        row.AcceptedBy,
        Iso(row.AcceptedAtUtc),
        row.AcceptedReason,
        row.ExceptionOwner,
        row.ExceptionReason,
        row.ExceptionCreatedBy,
        Iso(row.ExceptionCreatedAtUtc),
        Iso(row.ExceptionExpiresUtc),
        row.Subject,
        row.SubjectLabel,
    ];

    private static string? Iso(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
