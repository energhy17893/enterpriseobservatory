using System.Globalization;
using EnterpriseObservatory.Api.Contracts;

namespace EnterpriseObservatory.Api.Reports;

/// <summary>The continuity report's CSV column mapping. See <see cref="AlertsReportCsv"/>.</summary>
/// <remarks>
/// The only continuity-specific code in the CSV path -- everything else is
/// <see cref="CsvWriter"/>. One row per cluster; for each of the four groups
/// (HA, DRS, storage path, N+1) the continuity findings counted by state, so
/// "failing" and "accepted with a reason" are told apart, and "not evaluated"
/// is never folded into a zero. The HA-collected column says per row whether
/// the cluster's configuration was read at all.
/// </remarks>
public static class ContinuityReportCsv
{
    private static readonly string[] Groups = ["HA", "DRS", "Storage path", "N+1"];

    private static readonly string[] States = ["failing", "accepted", "excepted", "not evaluated", "stale"];

    private static readonly string[] Header =
    [
        "Cluster",
        "Source",
        "HA settings collected",
        .. Groups.SelectMany(g => States.Select(s => $"{g} {s}")),
        "Storage path affected hosts",
    ];

    public static string Write(IEnumerable<ContinuityReportRow> rows) =>
        CsvWriter.Write(Header, rows.Select(ToCells).ToList());

    private static IReadOnlyList<string?> ToCells(ContinuityReportRow row) =>
    [
        row.ClusterName,
        row.Source,
        row.HaSettingsCollected ? "yes" : "no",
        .. Counts(row.Ha),
        .. Counts(row.Drs),
        .. Counts(row.StoragePath),
        .. Counts(row.NPlusOne),
        string.Join("; ", row.StoragePathAffectedHosts),
    ];

    private static IEnumerable<string?> Counts(ContinuityStateCounts counts) =>
    [
        Number(counts.Failing),
        Number(counts.Accepted),
        Number(counts.Excepted),
        Number(counts.NotEvaluated),
        Number(counts.Stale),
    ];

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
