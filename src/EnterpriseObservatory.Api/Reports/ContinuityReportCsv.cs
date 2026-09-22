using System.Globalization;
using EnterpriseObservatory.Api.Contracts;

namespace EnterpriseObservatory.Api.Reports;

/// <summary>The continuity report's CSV mapping. See <see cref="AlertsReportCsv"/>.</summary>
/// <remarks>
/// The only continuity-specific code in the CSV path -- everything else is
/// <see cref="CsvWriter"/>. The same projection the page shows, one line per
/// thing counted, with a section column so a spreadsheet can filter it:
/// <list type="bullet">
/// <item><c>vCenter</c> -- each control that applies to the vCenter, then
/// <c>vCenter alarm</c> for each alarm raised on it;</item>
/// <item><c>Cluster</c> -- each cluster control per cluster, and one line for
/// the hosts, VMs and datastores under it;</item>
/// <item><c>Estate</c> -- one summary line per control that applies to hosts,
/// VMs or datastores, naming at most ten failing entities and counting the rest.</item>
/// </list>
/// Counts are by state, so "failing" and "accepted with a reason" are told
/// apart and "not evaluated" is never folded into a zero. The basis column is
/// never blank: see <see cref="ControlBasis"/>.
/// </remarks>
public static class ContinuityReportCsv
{
    private static readonly string[] Header =
    [
        "Section",
        "Name",
        "Source",
        "Control",
        "Title",
        "Basis",
        "Failing",
        "Accepted",
        "Excepted",
        "Not evaluated",
        "Passing",
        "Stale",
        "Detail",
    ];

    public static string Write(ContinuityReportView report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var controls = report.Controls.ToDictionary(c => c.ControlId, StringComparer.Ordinal);
        var lines = new List<IReadOnlyList<string?>>();

        foreach (var vCenter in report.VCenters)
        {
            foreach (var control in vCenter.Controls)
            {
                lines.Add(ControlLine("vCenter", vCenter.VCenterName, vCenter.Source, control, controls, string.Empty));
            }

            foreach (var alarm in vCenter.Alarms)
            {
                var detail = $"{alarm.Severity}, {alarm.State}" + (alarm.IsStale ? ", stale" : string.Empty);
                lines.Add(
                [
                    "vCenter alarm", vCenter.VCenterName, vCenter.Source, string.Empty, alarm.Title, string.Empty,
                    .. Enumerable.Repeat(string.Empty, 6), detail,
                ]);
            }
        }

        foreach (var row in report.Rows)
        {
            if (!row.HaSettingsCollected)
            {
                lines.Add(
                [
                    "Cluster", row.ClusterName, row.Source, string.Empty, "HA configuration", string.Empty,
                    .. Enumerable.Repeat(string.Empty, 6), "HA configuration not collected",
                ]);
            }

            foreach (var control in row.Controls)
            {
                lines.Add(ControlLine("Cluster", row.ClusterName, row.Source, control, controls, string.Empty));
            }

            lines.Add(
            [
                "Cluster", row.ClusterName, row.Source, string.Empty, "Hosts, VMs and datastores under it", string.Empty,
                .. Counts(row.Contained),
                Names(row.ContainedAffectedNames, row.ContainedAffectedMore, "; "),
            ]);
        }

        foreach (var row in report.ControlRows)
        {
            var names = Names(row.FailingNames, row.MoreFailing, ", ");

            lines.Add(
            [
                "Estate", row.AppliesTo.ToString(), string.Empty, row.ControlId, row.Title, ControlBasis.Label(row.Citation),
                .. Counts(row.Counts),
                names.Length == 0 ? string.Empty : $"failing: {names}",
            ]);
        }

        return CsvWriter.Write(Header, lines);
    }

    private static IReadOnlyList<string?> ControlLine(
        string section,
        string name,
        string source,
        ContinuityControlCounts control,
        Dictionary<string, ContinuityControlInfo> controls,
        string detail)
    {
        controls.TryGetValue(control.ControlId, out var info);

        return
        [
            section, name, source, control.ControlId, info?.Title ?? control.ControlId, ControlBasis.Label(info?.Citation),
            .. Counts(control.Counts),
            detail,
        ];
    }

    private static string Names(IReadOnlyList<string> names, int more, string separator) =>
        more > 0
            ? string.Join(separator, [.. names, $"+{more.ToString(CultureInfo.InvariantCulture)}"])
            : string.Join(separator, names);

    private static IEnumerable<string?> Counts(ContinuityStateCounts counts) =>
    [
        Number(counts.Failing),
        Number(counts.Accepted),
        Number(counts.Excepted),
        Number(counts.NotEvaluated),
        Number(counts.Passing),
        Number(counts.Stale),
    ];

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
