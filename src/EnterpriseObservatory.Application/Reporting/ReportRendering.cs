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
/// report content itself, which M5.1's CSV and printable pages produce. The
/// scheduler on this side of the seam knows nothing about alerts, CSV or PDF;
/// it knows there is a <see cref="ReportKind"/> and something that can turn
/// one into a <see cref="ReportContent"/>.
/// </para>
/// <para>
/// <see cref="PlaceholderReportRenderer"/> is the only implementation here. It
/// exists so this feature is end-to-end testable without the report content
/// existing yet, and is expected to be replaced — or wrapped, per
/// <see cref="ReportKind"/> — once M5.1's export lands.
/// </para>
/// </remarks>
public interface IReportRenderer
{
    Task<ReportContent> RenderAsync(ReportKind kind, CancellationToken cancellationToken);
}

/// <summary>A stand-in for the real report content. See <see cref="IReportRenderer"/>.</summary>
public sealed class PlaceholderReportRenderer : IReportRenderer
{
    public Task<ReportContent> RenderAsync(ReportKind kind, CancellationToken cancellationToken) =>
        Task.FromResult(new ReportContent
        {
            Subject = $"Enterprise Observatory — {kind} report",
            BodyText =
                $"This is a placeholder {kind} report. The delivery schedule that sent it is " +
                "working; the report content itself has not been wired in yet.",
        });
}
