using System.Globalization;
using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Application.Collection;
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
/// One case per <see cref="ReportKind"/>. M5.2 (compliance) and M5.3
/// (capacity) are expected to plug in by adding a case each that calls their
/// own <c>ReadModel</c> projection and their own <c>*ReportCsv</c> writer, the
/// same shape <see cref="RenderAlerts"/> already follows. A kind with no case
/// throws rather than returning an empty <see cref="ReportContent"/> — a
/// subscription set up for a report kind nothing renders yet must show up as
/// a dispatch failure the operator can see, not a blank mail nobody can
/// explain. See <see cref="ReportDispatchService"/>, which turns that
/// exception into exactly that failure.
/// </para>
/// </remarks>
public sealed class ReportRenderer(ReadModel model, IClock clock) : IReportRenderer
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
