using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Application.Reporting;

/// <summary>What one dispatch pass did, for the caller's log.</summary>
public sealed record ReportDispatchOutcome
{
    public required int Checked { get; init; }

    public required int Due { get; init; }

    public required int Sent { get; init; }

    public required IReadOnlyList<(string SubscriptionId, string Detail)> Failures { get; init; }
}

/// <summary>
/// Finds subscriptions that are due and sends their reports.
/// </summary>
/// <remarks>
/// <para>
/// Host-independent on purpose: a <c>BackgroundService</c> ticking every
/// minute is a hosting detail (ADR-0001's split between collection and the web
/// interface), and this is the part of M5.4 that stays the same on either side
/// of that split. See <see cref="Collection.MonitoringCycle"/> for the same
/// shape applied to collection.
/// </para>
/// <para>
/// Unconfigured SMTP settings are not an error here: an installation that has
/// not set up mail yet simply has nothing due, silently, rather than an
/// alarming stream of failures for every subscription before anyone has had
/// the chance to configure a relay. Once a subscription exists and SMTP is
/// unconfigured, that is reported as this pass's failure for it — the
/// operator asked for a report and nothing said why it never arrived.
/// </para>
/// </remarks>
public sealed class ReportDispatchService(
    IReportSubscriptionStore subscriptions,
    ISmtpSettingsStore smtpSettings,
    IReportRenderer renderer,
    IMailSender sender,
    IClock clock)
{
    private readonly IReportSubscriptionStore _subscriptions =
        subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));

    private readonly ISmtpSettingsStore _smtpSettings =
        smtpSettings ?? throw new ArgumentNullException(nameof(smtpSettings));

    private readonly IReportRenderer _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    private readonly IMailSender _sender = sender ?? throw new ArgumentNullException(nameof(sender));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<ReportDispatchOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var all = _subscriptions.All;

        var due = all
            .Where(s => s.IsEnabled)
            .Where(s => ReportScheduling.IsDue(s.Schedule, s.LastSentUtc, now))
            .ToList();

        var sent = 0;
        var failures = new List<(string, string)>();

        foreach (var subscription in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Claimed before anything that can fail. See
            // IReportSubscriptionStore.MarkDispatched for why the claim comes
            // first: a crash after the mail actually left the relay must never
            // read as "still due" on restart.
            _subscriptions.MarkDispatched(subscription.Id, now);

            var outcome = await SendOneAsync(subscription, cancellationToken).ConfigureAwait(false);

            if (outcome is { } detail)
            {
                _subscriptions.MarkFailed(subscription.Id, detail);
                failures.Add((subscription.Id, detail));
            }
            else
            {
                sent++;
            }
        }

        return new ReportDispatchOutcome
        {
            Checked = all.Count,
            Due = due.Count,
            Sent = sent,
            Failures = failures,
        };
    }

    /// <returns>Null on success; the failure detail otherwise.</returns>
    private async Task<string?> SendOneAsync(
        ReportSubscription subscription, CancellationToken cancellationToken)
    {
        var settings = _smtpSettings.Current;

        if (!settings.IsConfigured)
        {
            return "SMTP is not configured for this installation.";
        }

        ReportContent content;

        try
        {
            content = await _renderer.RenderAsync(subscription.Kind, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The report content itself failed to build. Reported the same way
            // a send failure is -- the subscriber does not care which half of
            // the pipe broke -- but never swallowed: an unexpected exception
            // from a renderer bug still surfaces through the caller's log via
            // the outcome this returns.
            return $"The report could not be built: {ex.Message}";
        }

        var mail = new OutgoingMail
        {
            To = subscription.Recipients,
            Subject = content.Subject,
            BodyText = content.BodyText,
            Attachments = content.Attachments,
        };

        var result = await _sender.SendAsync(settings, mail, cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? null : result.Detail;
    }
}
