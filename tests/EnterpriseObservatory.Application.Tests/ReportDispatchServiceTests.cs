using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Reporting;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The scheduler's own logic, independent of the minute-tick worker that
/// drives it in the host. See <see cref="ReportSchedulingTests"/> for the
/// calendar rules this builds on.
/// </summary>
public class ReportDispatchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeSubscriptionStore : IReportSubscriptionStore
    {
        private readonly Dictionary<string, ReportSubscription> _rows = new(StringComparer.Ordinal);

        public List<string> DispatchClaims { get; } = [];

        public IReadOnlyList<ReportSubscription> All => [.. _rows.Values];

        public ReportSubscription? Find(string id) => _rows.GetValueOrDefault(id);

        public bool Add(ReportSubscription subscription) => _rows.TryAdd(subscription.Id, subscription);

        public bool Update(ReportSubscription subscription)
        {
            if (!_rows.ContainsKey(subscription.Id))
            {
                return false;
            }

            _rows[subscription.Id] = subscription;
            return true;
        }

        public bool Remove(string id) => _rows.Remove(id);

        public void MarkDispatched(string id, DateTimeOffset atUtc)
        {
            DispatchClaims.Add(id);
            _rows[id] = _rows[id] with { LastSentUtc = atUtc, LastError = null };
        }

        public void MarkFailed(string id, string detail) =>
            _rows[id] = _rows[id] with { LastError = detail };
    }

    private sealed class FakeSmtpStore(SmtpSettings settings) : ISmtpSettingsStore
    {
        public void Save(SmtpSettings value) => throw new NotSupportedException();

        public SmtpSettings Current => settings;
    }

    private sealed class FakeRenderer : IReportRenderer
    {
        public Task<ReportContent> RenderAsync(ReportKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(new ReportContent { Subject = "s", BodyText = "b" });
    }

    /// <summary>
    /// Records the order claim-then-send happens in, and can be told to fail.
    /// </summary>
    private sealed class RecordingSender(bool succeed) : IMailSender
    {
        public int Calls { get; private set; }

        public Task<MailSendResult> SendAsync(
            SmtpSettings settings, OutgoingMail mail, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(
                succeed ? MailSendResult.Ok("sent") : MailSendResult.Failed("relay refused it"));
        }
    }

    private static readonly SmtpSettings Configured = new()
    {
        Host = "smtp.example.com",
        FromAddress = "observatory@example.com",
    };

    private static ReportSubscription Subscription(DateTimeOffset? lastSentUtc = null) => new()
    {
        Id = "sub-1",
        Recipients = ["team@example.com"],
        Schedule = new ReportSchedule { Frequency = ReportFrequency.Daily, HourLocal = 7, TimeZoneId = "UTC" },
        Kind = ReportKind.Alerts,
        CreatedBy = "operator",
        CreatedUtc = Now.AddDays(-30),
        LastSentUtc = lastSentUtc,
    };

    [Fact]
    public async Task A_due_subscription_is_claimed_before_the_send_is_attempted()
    {
        var subscriptions = new FakeSubscriptionStore();
        subscriptions.Add(Subscription());
        var sender = new RecordingSender(succeed: true);

        var service = new ReportDispatchService(
            subscriptions, new FakeSmtpStore(Configured), new FakeRenderer(), sender, new FixedClock(Now));

        var outcome = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, outcome.Due);
        Assert.Equal(1, outcome.Sent);
        Assert.Empty(outcome.Failures);

        // The claim happened -- MarkDispatched was called -- regardless of
        // whether the send below it succeeded, which is the whole point.
        Assert.Contains("sub-1", subscriptions.DispatchClaims);
        Assert.NotNull(subscriptions.Find("sub-1")!.LastSentUtc);
    }

    [Fact]
    public async Task A_second_pass_in_the_same_window_does_not_resend()
    {
        // Simulates a restart moments after the first pass: the store already
        // reflects the claim made a moment ago.
        var subscriptions = new FakeSubscriptionStore();
        subscriptions.Add(Subscription(lastSentUtc: Now));
        var sender = new RecordingSender(succeed: true);

        var service = new ReportDispatchService(
            subscriptions, new FakeSmtpStore(Configured), new FakeRenderer(), sender,
            new FixedClock(Now.AddMinutes(1)));

        var outcome = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, outcome.Due);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task A_failed_send_is_recorded_but_the_claim_stands()
    {
        var subscriptions = new FakeSubscriptionStore();
        subscriptions.Add(Subscription());
        var sender = new RecordingSender(succeed: false);

        var service = new ReportDispatchService(
            subscriptions, new FakeSmtpStore(Configured), new FakeRenderer(), sender, new FixedClock(Now));

        var outcome = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, outcome.Sent);
        Assert.Single(outcome.Failures);
        Assert.Equal("relay refused it", outcome.Failures[0].Detail);

        var stored = subscriptions.Find("sub-1")!;
        Assert.NotNull(stored.LastSentUtc); // the claim from before the attempt stands
        Assert.Equal("relay refused it", stored.LastError);
    }

    [Fact]
    public async Task An_unconfigured_relay_fails_a_due_subscription_rather_than_silently_skipping_it()
    {
        var subscriptions = new FakeSubscriptionStore();
        subscriptions.Add(Subscription());
        var sender = new RecordingSender(succeed: true);

        var service = new ReportDispatchService(
            subscriptions, new FakeSmtpStore(new SmtpSettings()), new FakeRenderer(), sender, new FixedClock(Now));

        var outcome = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, sender.Calls);
        Assert.Single(outcome.Failures);
    }

    [Fact]
    public async Task A_disabled_subscription_is_never_due()
    {
        var subscriptions = new FakeSubscriptionStore();
        subscriptions.Add(Subscription() with { IsEnabled = false });

        var service = new ReportDispatchService(
            subscriptions, new FakeSmtpStore(Configured), new FakeRenderer(),
            new RecordingSender(succeed: true), new FixedClock(Now));

        var outcome = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, outcome.Due);
    }
}
