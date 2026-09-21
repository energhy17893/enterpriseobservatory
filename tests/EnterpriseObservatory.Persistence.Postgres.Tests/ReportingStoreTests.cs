using EnterpriseObservatory.Application.Reporting;
using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// SMTP settings and report subscriptions (roadmap M5.4), against a real
/// server.
/// </summary>
public class ReportingStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 7, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(Secret secret) =>
            secret.IsEmpty ? string.Empty : new string(secret.Reveal().Reverse().ToArray());

        public Secret Unprotect(string protectedValue) =>
            protectedValue.Length == 0
                ? Secret.Empty
                : Secret.From(new string(protectedValue.Reverse().ToArray()));
    }

    [SkippableFact]
    public void Unconfigured_smtp_settings_read_back_as_empty_rather_than_throwing()
    {
        RequireDatabase();

        var settings = new PostgresSmtpSettingsStore(_live.Database, new ReversingProtector()).Current;

        Assert.False(settings.IsConfigured);
        Assert.True(settings.Password.IsEmpty);
    }

    [SkippableFact]
    public void Smtp_settings_survive_a_restart_and_the_password_is_not_stored_as_itself()
    {
        RequireDatabase();

        var protector = new ReversingProtector();

        new PostgresSmtpSettingsStore(_live.Database, protector).Save(new SmtpSettings
        {
            Host = "smtp.example.com",
            Port = 587,
            TlsMode = SmtpTlsMode.StartTls,
            FromAddress = "observatory@example.com",
            Username = "observatory",
            Password = Secret.From("hunter2"),
        });

        _live.Restart();

        var recovered = new PostgresSmtpSettingsStore(_live.Database, protector).Current;

        Assert.Equal("smtp.example.com", recovered.Host);
        Assert.Equal(Secret.From("hunter2"), recovered.Password);
        Assert.NotNull(recovered.PasswordSetUtc);
    }

    [SkippableFact]
    public void An_empty_password_on_a_save_keeps_the_stored_one_for_the_same_relay()
    {
        RequireDatabase();

        var protector = new ReversingProtector();
        var store = new PostgresSmtpSettingsStore(_live.Database, protector);

        store.Save(new SmtpSettings
        {
            Host = "smtp.example.com",
            Port = 587,
            TlsMode = SmtpTlsMode.StartTls,
            FromAddress = "observatory@example.com",
            Username = "observatory",
            Password = Secret.From("hunter2"),
        });

        // The edit form cannot show the stored password back, so saving it
        // unchanged arrives here with an empty one -- taking that at face
        // value would silently break scheduled reports. Every field that
        // decides who receives the password (host, port, username, TLS
        // mode) is unchanged here, so the reuse is safe.
        store.Save(new SmtpSettings
        {
            Host = "smtp.example.com",
            Port = 587,
            TlsMode = SmtpTlsMode.StartTls,
            FromAddress = "observatory-team@example.com",
            Username = "observatory",
        });

        _live.Restart();

        var recovered = new PostgresSmtpSettingsStore(_live.Database, protector).Current;

        Assert.Equal("observatory-team@example.com", recovered.FromAddress);
        Assert.Equal(Secret.From("hunter2"), recovered.Password);
    }

    [SkippableFact]
    public void An_empty_password_on_a_save_to_a_different_host_does_not_carry_the_stored_password_over()
    {
        RequireDatabase();

        // The store-level half of the fix for a stored SMTP password being
        // reused against a different relay: EmailApi rejects this case with
        // a 400 before Save is ever called, but the store applies the same
        // guard so a caller that reaches it directly cannot walk away with
        // the credential by pointing Host somewhere else and leaving the
        // password blank.
        var protector = new ReversingProtector();
        var store = new PostgresSmtpSettingsStore(_live.Database, protector);

        store.Save(new SmtpSettings
        {
            Host = "smtp.example.com",
            Port = 587,
            TlsMode = SmtpTlsMode.StartTls,
            FromAddress = "observatory@example.com",
            Username = "observatory",
            Password = Secret.From("hunter2"),
        });

        store.Save(new SmtpSettings
        {
            Host = "attacker.example.com",
            Port = 587,
            TlsMode = SmtpTlsMode.None,
            FromAddress = "observatory@example.com",
            Username = "observatory",
            AllowUnencrypted = true,
        });

        _live.Restart();

        var recovered = new PostgresSmtpSettingsStore(_live.Database, protector).Current;

        Assert.Equal("attacker.example.com", recovered.Host);
        Assert.True(recovered.Password.IsEmpty);
    }

    [SkippableFact]
    public void A_subscription_survives_a_restart_with_its_recipients_and_schedule()
    {
        RequireDatabase();

        var store = new PostgresReportSubscriptionStore(_live.Database);

        store.Add(new ReportSubscription
        {
            Id = "sub-1",
            Recipients = ["team@example.com", "second@example.com"],
            Schedule = new ReportSchedule
            {
                Frequency = ReportFrequency.Weekly,
                DayOfWeek = DayOfWeek.Friday,
                HourLocal = 6,
                TimeZoneId = "Europe/Istanbul",
            },
            Kind = ReportKind.Alerts,
            CreatedBy = "ertugrul",
            CreatedUtc = T0,
        });

        _live.Restart();

        var recovered = Assert.Single(new PostgresReportSubscriptionStore(_live.Database).All);

        Assert.Equal(["team@example.com", "second@example.com"], recovered.Recipients);
        Assert.Equal(ReportFrequency.Weekly, recovered.Schedule.Frequency);
        Assert.Equal(DayOfWeek.Friday, recovered.Schedule.DayOfWeek);
        Assert.Equal("Europe/Istanbul", recovered.Schedule.TimeZoneId);
    }

    [SkippableFact]
    public void Marking_a_subscription_dispatched_survives_a_restart_and_clears_a_previous_error()
    {
        RequireDatabase();

        var store = new PostgresReportSubscriptionStore(_live.Database);

        store.Add(new ReportSubscription
        {
            Id = "sub-1",
            Recipients = ["team@example.com"],
            Schedule = new ReportSchedule { Frequency = ReportFrequency.Daily, HourLocal = 7 },
            CreatedBy = "ertugrul",
            CreatedUtc = T0,
        });

        store.MarkFailed("sub-1", "the relay refused it");
        store.MarkDispatched("sub-1", T0.AddDays(1));

        _live.Restart();

        var recovered = Assert.Single(new PostgresReportSubscriptionStore(_live.Database).All);

        Assert.Equal(T0.AddDays(1), recovered.LastSentUtc);
        Assert.Null(recovered.LastError);
    }
}
