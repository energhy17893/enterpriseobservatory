using System.Globalization;
using EnterpriseObservatory.Api.Contracts;

namespace EnterpriseObservatory.Api.Reports;

/// <summary>The alert/finding report's CSV column mapping.</summary>
/// <remarks>
/// The only alert-specific code in the CSV path — everything else is
/// <see cref="CsvWriter"/>. M5.2 and M5.3 are expected to add a sibling file
/// the same shape: a header list, a row-to-cells mapping, and nothing else.
/// </remarks>
public static class AlertsReportCsv
{
    private static readonly string[] Header =
    [
        "Severity",
        "Title",
        "Entity",
        "Entity kind",
        "Category",
        "Source",
        "State",
        "First seen (UTC)",
        "Last seen (UTC)",
        "Acknowledged by",
        "Acknowledged at (UTC)",
        "Cleared by",
        "Cleared at (UTC)",
        "Derived",
    ];

    public static string Write(IEnumerable<AlertReportRow> rows) =>
        CsvWriter.Write(Header, rows.Select(ToCells).ToList());

    private static IReadOnlyList<string?> ToCells(AlertReportRow row) =>
    [
        row.Severity.ToString(),
        row.Title,
        row.EntityName,
        row.EntityKind?.ToString(),
        row.Category,
        row.Source,
        row.State.ToString(),
        Iso(row.FirstSeenUtc),
        Iso(row.LastSeenUtc),
        row.AcknowledgedBy,
        Iso(row.AcknowledgedAtUtc),
        row.ClearedBy,
        Iso(row.ClearedAtUtc),
        row.IsDerived ? "yes" : "no",
    ];

    private static string? Iso(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
