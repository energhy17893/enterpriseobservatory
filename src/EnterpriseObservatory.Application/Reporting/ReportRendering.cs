namespace EnterpriseObservatory.Application.Reporting;

/// <summary>What one rendered report contains.</summary>
public sealed record ReportContent
{
    public required string Subject { get; init; }

    public required string BodyText { get; init; }

    public IReadOnlyList<MailAttachment> Attachments { get; init; } = [];
}

/// <summary>
/// Builds the content of a scheduled report.
/// </summary>
/// <remarks>
/// <para>
/// The seam between this feature — the delivery pipe, roadmap M5.4 — and the
/// report content itself, which M5.1, M5.2 and M5.3's exports produce. The
/// scheduler on this side of the seam knows nothing about alerts, compliance
/// or capacity; it knows there is a <see cref="ReportKind"/>, a
/// <see cref="ReportFrequency"/> that decides the period a periodic report
/// covers, and something that can turn those into a <see cref="ReportContent"/>.
/// </para>
/// <para>
/// The real implementation — <c>EnterpriseObservatory.Api.Reports.ReportRenderer</c>
/// — lives in the Api project, the only layer on this side of ADR-0001's split
/// that can reach both the read model and the CSV writers each report kind
/// builds its attachment from. An unrecognised <see cref="ReportKind"/> is
/// expected to fail loudly there rather than send an empty mail; see
/// <see cref="ReportDispatchService"/>, which records that failure against the
/// subscription instead of swallowing it.
/// </para>
/// </remarks>
public interface IReportRenderer
{
    Task<ReportContent> RenderAsync(ReportKind kind, ReportFrequency frequency, CancellationToken cancellationToken);
}
