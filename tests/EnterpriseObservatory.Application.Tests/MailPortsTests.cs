using EnterpriseObservatory.Application.Reporting;
using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// <see cref="SmtpSettings.HasSameConnectionDetails"/>, the check that
/// decides whether a blank password on a save or a test can safely reuse the
/// stored one -- see EmailApi and PostgresSmtpSettingsStore for where it is
/// applied.
/// </summary>
public class MailPortsTests
{
    private static readonly SmtpSettings Stored = new()
    {
        Host = "smtp.example.com",
        Port = 587,
        TlsMode = SmtpTlsMode.StartTls,
        FromAddress = "observatory@example.com",
        Username = "observatory",
        Password = Secret.From("hunter2"),
    };

    [Fact]
    public void Identical_connection_details_match_even_when_from_address_differs()
    {
        // FromAddress does not decide who receives the password -- only
        // where the connection goes and who authenticates it does.
        var candidate = Stored with { FromAddress = "someone-else@example.com" };

        Assert.True(candidate.HasSameConnectionDetails(Stored));
    }

    [Theory]
    [InlineData("attacker.example.com", 587, "observatory", SmtpTlsMode.StartTls)]
    [InlineData("smtp.example.com", 2525, "observatory", SmtpTlsMode.StartTls)]
    [InlineData("smtp.example.com", 587, "someone-else", SmtpTlsMode.StartTls)]
    [InlineData("smtp.example.com", 587, "observatory", SmtpTlsMode.None)]
    public void A_change_to_host_port_username_or_tls_mode_is_not_a_match(
        string host, int port, string username, SmtpTlsMode tlsMode)
    {
        var candidate = Stored with { Host = host, Port = port, Username = username, TlsMode = tlsMode };

        Assert.False(candidate.HasSameConnectionDetails(Stored));
    }

    [Fact]
    public void MailSendResult_Failed_defaults_to_the_Other_failure_kind()
    {
        var result = MailSendResult.Failed("the relay said no");

        Assert.False(result.Succeeded);
        Assert.Equal(MailFailureKind.Other, result.FailureKind);
    }

    [Fact]
    public void MailSendResult_Ok_carries_no_failure_kind()
    {
        var result = MailSendResult.Ok("sent");

        Assert.True(result.Succeeded);
        Assert.Equal(MailFailureKind.None, result.FailureKind);
    }
}
