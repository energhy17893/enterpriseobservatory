using System.Globalization;
using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Application.Reporting;

namespace EnterpriseObservatory.Api.Reports;

/// <summary>
/// The real <see cref="IReportRenderer"/>: turns a <see cref="ReportKind"/>
/// into the mail M5.4's scheduler sends, reusing whichever export each report
/// kind already has rather than inventing a second copy of it.
/// </summary>
/// <remarks>
/// <para>
/// Lives here, in the Api project, because this is the only layer that can
/// reach both <see cref="ReadModel"/> and the CSV writers under
/// <c>Reports/</c> without breaking ADR-0001's layering — the architecture
/// tests enforce that the Application layer knows nothing about how a report
/// is rendered, only that <see cref="IReportRenderer"/> can do it.
/// </para>
/// <para>
/// One case per <see cref="ReportKind"/>, each calling the same projection
/// and <c>*ReportCsv</c> writer as that report's download endpoint. A kind
/// with no case (or compliance without a <see cref="ComplianceService"/>)
/// throws rather than returning an empty <see cref="ReportContent"/> — a
/// subscription set up for a report kind nothing renders yet must show up as
/// a dispatch failure the operator can see, not a blank mail nobody can
/// explain. See <see cref="ReportDispatchService"/>, which turns that
/// exception into exactly that failure.
/// </para>
/// </remarks>
public sealed class ReportRenderer(ReadModel model, IClock clock, ComplianceService? compliance = null) : IReportRenderer
{
    private readonly ReadModel _model = model ?? throw new ArgumentNullException(nameof(model));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public Task<ReportContent> RenderAsync(
        ReportKind kind, ReportFrequency frequency, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var content = kind switch
        {
            ReportKind.Alerts => RenderAlerts(frequency),
            ReportKind.Compliance when compliance is not null => RenderCompliance(frequency, compliance),
            ReportKind.Capacity => RenderCapacity(),
            ReportKind.Continuity => RenderContinuity(),
            _ => throw new NotSupportedException(
                $"No report renderer is wired up for '{kind}' yet."),
        };

        return Task.FromResult(content);
    }

    /// <summary>
    /// The alert/finding report: the same window, rows and CSV bytes
    /// <c>GET /api/reports/alerts.csv</c> would hand back for this period —
    /// see <see cref="ObservatoryApi"/> — so a subscriber never sees the
    /// mailed report disagree with what the download link produces.
    /// </summary>
    private ReportContent RenderAlerts(ReportFrequency frequency)
    {
        var to = _clock.UtcNow;
        var from = to - PeriodFor(frequency);

        var report = _model.AlertsReport(fromUtc: from, toUtc: to);

        var csv = AlertsReportCsv.Write(report.Rows);
        var fileName = $"alerts-{report.GeneratedAtUtc:yyyyMMdd-HHmm}.csv";

        return new ReportContent
        {
            Subject = $"Enterprise Observatory — alert report {report.GeneratedAtUtc:yyyy-MM-dd}",
            BodyText = AlertsReportBody(report),
            Attachments =
            [
                new MailAttachment
                {
                    FileName = fileName,
                    ContentType = "text/csv",
                    Content = CsvWriter.ToUtf8WithBom(csv),
                },
            ],
        };
    }

    /// <summary>
    /// The compliance report as <c>GET /api/reports/compliance.csv</c> builds
    /// it, both sections attached: the findings detail (a snapshot, so the
    /// period does not narrow it) and the verdict changes over the period.
    /// </summary>
    private ReportContent RenderCompliance(ReportFrequency frequency, ComplianceService service)
    {
        var to = _clock.UtcNow;

        // catalogueId null resolves to the vendor guide, same as the
        // endpoint's old single-catalogue URL; null only when no catalogue
        // owned by Broadcom is loaded, which is a dispatch failure like any
        // other unrenderable report -- see the remarks above.
        var report = ComplianceApi.Report(service, null, null, null, to - PeriodFor(frequency), to)
            ?? throw new InvalidOperationException("No catalogue is loaded to report on.");
        var stamp = report.GeneratedAtUtc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        var body = string.Join(
            "\n",
            $"Compliance report against {report.CatalogueName} {report.CatalogueRelease}.",
            report.LastEvaluatedUtc is { } last ? $"Last evaluated: {FormatUtc(last)}" : "Not evaluated yet.",
            string.Empty,
            $"Findings: {report.Findings.Count.ToString(CultureInfo.InvariantCulture)}",
            $"Stale (host did not report last cycle): {report.StaleCount.ToString(CultureInfo.InvariantCulture)}",
            $"Standing exceptions: {report.Exceptions.Count.ToString(CultureInfo.InvariantCulture)}",
            $"Verdict changes {FormatUtc(report.HistoryFromUtc)} to {FormatUtc(report.HistoryToUtc)}: "
                + report.History.Count.ToString(CultureInfo.InvariantCulture),
            string.Empty,
            "Findings and change history are attached as CSV.");

        return new ReportContent
        {
            Subject = $"Enterprise Observatory — compliance report {report.GeneratedAtUtc:yyyy-MM-dd}",
            BodyText = body,
            Attachments =
            [
                Csv($"compliance-{stamp}.csv", ComplianceFindingsReportCsv.Write(report.Findings)),
                Csv($"compliance-history-{stamp}.csv", ComplianceHistoryReportCsv.Write(report.History)),
            ],
        };
    }

    /// <summary>
    /// The capacity report as <c>GET /api/reports/capacity.csv</c> builds it.
    /// A snapshot with its own fill-date forecast, so the period does not apply.
    /// </summary>
    private ReportContent RenderCapacity()
    {
        var report = _model.CapacityReport();
        var s = report.Summary;
        var stamp = report.GeneratedAtUtc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        var body = string.Join(
            "\n",
            $"Capacity report for {s.TotalDatastores.ToString(CultureInfo.InvariantCulture)} datastores.",
            string.Empty,
            $"Filling within 7 days: {s.FillingWithin7Days.ToString(CultureInfo.InvariantCulture)}",
            $"Filling within 30 days: {s.FillingWithin30Days.ToString(CultureInfo.InvariantCulture)}",
            $"Over-committed: {s.OvercommittedCount.ToString(CultureInfo.InvariantCulture)}",
            $"No fill-date estimate yet: {FormatCounts(s.NoEstimateByReason)}",
            string.Empty,
            "The full list is attached as a CSV.");

        return new ReportContent
        {
            Subject = $"Enterprise Observatory — capacity report {report.GeneratedAtUtc:yyyy-MM-dd}",
            BodyText = body,
            Attachments = [Csv($"capacity-{stamp}.csv", CapacityReportCsv.Write(report.Rows))],
        };
    }

    /// <summary>
    /// The continuity report as <c>GET /api/reports/continuity.csv</c> builds
    /// it. A snapshot of the current continuity findings, same as capacity --
    /// the period does not apply.
    /// </summary>
    private ReportContent RenderContinuity()
    {
        var report = _model.ContinuityReport();
        var s = report.Summary;
        var stamp = report.GeneratedAtUtc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        var bodyLines = new List<string>
        {
            $"Continuity report for {s.TotalClusters.ToString(CultureInfo.InvariantCulture)} clusters.",
            string.Empty,
            $"Findings: {Describe(s.Totals)}",
            $"Clusters with a failing finding: {s.ClustersWithFailingCount.ToString(CultureInfo.InvariantCulture)}",
        };

        // Same shape as the page and the CSV: vCenter, clusters, then one line
        // per host/VM/datastore control -- never one line per entity.
        foreach (var vCenter in report.VCenters)
        {
            bodyLines.Add(string.Empty);
            bodyLines.Add($"vCenter {vCenter.VCenterName}:");

            foreach (var control in vCenter.Controls)
            {
                bodyLines.Add($"  {control.ControlId}: {Describe(control.Counts)}");
            }

            foreach (var alarm in vCenter.Alarms)
            {
                bodyLines.Add($"  alarm: {alarm.Title} ({alarm.Severity}, {alarm.State}{(alarm.IsStale ? ", stale" : string.Empty)})");
            }
        }

        if (s.ClustersWithFailingCount > 0)
        {
            bodyLines.Add(string.Empty);
            bodyLines.Add($"Clusters with a failing finding: {string.Join(", ", s.ClustersWithFailingNames)}");
        }

        if (report.ControlRows.Count > 0)
        {
            bodyLines.Add(string.Empty);
            bodyLines.Add("Hosts, virtual machines and datastores, by control:");
        }

        foreach (var row in report.ControlRows)
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"  {row.ControlId}: {row.Counts.Failing} failing / {row.Counts.Passing} passing");

            if (row.FailingNames.Count > 0)
            {
                line += "; failing: " + string.Join(", ", row.FailingNames) +
                    (row.MoreFailing > 0 ? string.Create(CultureInfo.InvariantCulture, $", +{row.MoreFailing}") : string.Empty);
            }

            bodyLines.Add(line);
        }

        if (s.Note is { } note)
        {
            bodyLines.Add(string.Empty);
            bodyLines.Add(note);
        }

        bodyLines.Add(string.Empty);
        bodyLines.Add("The full list is attached as a CSV.");

        return new ReportContent
        {
            Subject = $"Enterprise Observatory — continuity report {report.GeneratedAtUtc:yyyy-MM-dd}",
            BodyText = string.Join("\n", bodyLines),
            Attachments = [Csv($"continuity-{stamp}.csv", ContinuityReportCsv.Write(report))],
        };
    }

    private static string Describe(ContinuityStateCounts c) => string.Create(CultureInfo.InvariantCulture,
        $"{c.Failing} failing, {c.Accepted} accepted, {c.Excepted} excepted, {c.NotEvaluated} not evaluated, " +
        $"{c.Passing} passing ({c.Stale} stale)");

    private static MailAttachment Csv(string fileName, string csv) => new()
    {
        FileName = fileName,
        ContentType = "text/csv",
        Content = CsvWriter.ToUtf8WithBom(csv),
    };

    /// <summary>
    /// The period a periodic report covers: a daily subscription's last 24
    /// hours, a weekly one's last 7 days. Unlike the download endpoint's
    /// default 7-day window — chosen for someone opening it on demand, who
    /// might not have looked in a week — a scheduled report covers exactly
    /// the gap since the last one, so nothing sent between mailings is missed
    /// or repeated.
    /// </summary>
    private static TimeSpan PeriodFor(ReportFrequency frequency) => frequency switch
    {
        ReportFrequency.Daily => TimeSpan.FromHours(24),
        ReportFrequency.Weekly => TimeSpan.FromDays(7),
        _ => throw new NotSupportedException($"No report period is defined for '{frequency}' yet."),
    };

    private static string AlertsReportBody(AlertReportView report)
    {
        var bySeverity = FormatCounts(report.Summary.BySeverity);
        var byState = FormatCounts(report.Summary.ByState);

        return string.Join(
            "\n",
            $"Alert report covering {FormatUtc(report.FromUtc)} to {FormatUtc(report.ToUtc)}.",
            string.Empty,
            $"Total alerts: {report.Summary.Total.ToString(CultureInfo.InvariantCulture)}",
            $"By severity: {bySeverity}",
            $"By state: {byState}",
            string.Empty,
            "The full list is attached as a CSV.");
    }

    private static string FormatCounts(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0
            ? "none"
            : string.Join(", ", counts.Select(kv => $"{kv.Key}: {kv.Value.ToString(CultureInfo.InvariantCulture)}"));

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
