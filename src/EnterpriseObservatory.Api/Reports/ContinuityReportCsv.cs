using System.Globalization;
using EnterpriseObservatory.Api.Contracts;

namespace EnterpriseObservatory.Api.Reports;

/// <summary>The continuity report's CSV column mapping. See <see cref="AlertsReportCsv"/>.</summary>
/// <remarks>
/// The only continuity-specific code in the CSV path -- everything else is
/// <see cref="CsvWriter"/>. One row per cluster, critical and warning counts
/// side by side for each of the five rule groups (HA, DRS, storage-path
/// redundancy, multipathing and N+1), so a reader can tell "zero because it
/// is fine" from "zero because it was never checked" without opening the
/// printable page -- the HA-collected column carries that distinction per
/// row.
/// </remarks>
public static class ContinuityReportCsv
{
    private static readonly string[] Header =
    [
        "Cluster",
        "Source",
        "HA settings collected",
        "HA critical",
        "HA warning",
        "DRS critical",
        "DRS warning",
        "Storage path critical",
        "Storage path warning",
        "Storage path affected hosts",
        "N+1 critical",
        "N+1 warning",
    ];

    public static string Write(IEnumerable<ContinuityReportRow> rows) =>
        CsvWriter.Write(Header, rows.Select(ToCells).ToList());

    private static IReadOnlyList<string?> ToCells(ContinuityReportRow row) =>
    [
        row.ClusterName,
        row.Source,
        row.HaSettingsCollected ? "yes" : "no",
        Number(row.HaCriticalCount),
        Number(row.HaWarningCount),
        Number(row.DrsCriticalCount),
        Number(row.DrsWarningCount),
        Number(row.StoragePathCriticalCount),
        Number(row.StoragePathWarningCount),
        string.Join("; ", row.StoragePathAffectedHosts),
        Number(row.NPlusOneCriticalCount),
        Number(row.NPlusOneWarningCount),
    ];

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
