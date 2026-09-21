using System.Globalization;
using EnterpriseObservatory.Api.Contracts;

namespace EnterpriseObservatory.Api.Reports;

/// <summary>The capacity report's CSV column mapping. See <see cref="AlertsReportCsv"/>.</summary>
/// <remarks>
/// The only capacity-specific code in the CSV path -- everything else is
/// <see cref="CsvWriter"/>. Bytes are written raw rather than converted to GB:
/// a spreadsheet is where somebody divides, and a byte count round-trips
/// exactly where a rounded gigabyte figure does not.
/// </remarks>
public static class CapacityReportCsv
{
    private static readonly string[] Header =
    [
        "Datastore",
        "Type",
        "Source",
        "Capacity (bytes)",
        "Used (bytes)",
        "Free (bytes)",
        "% used",
        "Provisioned (bytes)",
        "Over-commit ratio",
        "Fill date (UTC)",
        "Days to fill",
        "Growth (bytes/day)",
        "Estimate window from (UTC)",
        "Estimate window to (UTC)",
        "Estimate",
    ];

    public static string Write(IEnumerable<CapacityReportRow> rows) =>
        CsvWriter.Write(Header, rows.Select(ToCells).ToList());

    private static IReadOnlyList<string?> ToCells(CapacityReportRow row) =>
    [
        row.Name,
        row.DatastoreType,
        row.Source,
        Number(row.CapacityBytes),
        Number(row.UsedBytes),
        Number(row.FreeBytes),
        Number(row.PercentUsed),
        Number(row.ProvisionedBytes),
        Number(row.OvercommitRatio),
        Iso(row.TimeToFull.FullAtUtc),
        Number(row.TimeToFull.Days),
        Number(row.TimeToFull.GrowthBytesPerDay),
        Iso(row.TimeToFull.WindowFromUtc),
        Iso(row.TimeToFull.WindowToUtc),
        // Never blank. A refusal is exactly as much an answer as a date, and
        // this is the one column that always says which one a row got.
        row.TimeToFull.Summary,
    ];

    private static string? Number(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture);

    private static string? Iso(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
