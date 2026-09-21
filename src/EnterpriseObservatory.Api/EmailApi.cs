using EnterpriseObservatory.Application.Reporting;
using EnterpriseObservatory.Application.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EnterpriseObservatory.Api;

/// <summary>The installation's SMTP settings, as the interface shows them.</summary>
/// <remarks>
/// There is no password field here and no endpoint that returns one — the
/// same rule <see cref="ConnectionView"/> follows. <see cref="PasswordStatus"/>
/// is the string "set" or "not set", never the value: a credential that can be
/// read back out is one that leaks through a screenshot or a support session.
/// </remarks>
public sealed record SmtpSettingsView
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required string TlsMode { get; init; }

    public required string FromAddress { get; init; }

    public required string Username { get; init; }

    public required bool AllowUnencrypted { get; init; }

    /// <summary>"set" or "not set". Never the password itself.</summary>
    public required string PasswordStatus { get; init; }

    public DateTimeOffset? PasswordSetUtc { get; init; }

    public required bool IsConfigured { get; init; }
}

/// <summary>What the settings form sends.</summary>
public record SmtpSettingsCommand
{
    public required string Host { get; init; }

    public int Port { get; init; } = 587;

    public string TlsMode { get; init; } = nameof(SmtpTlsMode.StartTls);

    public required string FromAddress { get; init; }

    public string Username { get; init; } = string.Empty;

    /// <summary>Empty means "keep the stored password" — same rule as <see cref="ConnectionCommand.Password"/>.</summary>
    public string Password { get; init; } = string.Empty;

    public bool AllowUnencrypted { get; init; }
}

/// <summary>What the settings form sends to try a send before saving, plus who to send to.</summary>
public sealed record TestEmailCommand : SmtpSettingsCommand
{
    public required string To { get; init; }
}

public sealed record MailTestView
{
    public required bool Succeeded { get; init; }

    public required string Detail { get; init; }
}

/// <summary>
/// The installation's mail relay settings.
/// </summary>
/// <remarks>
/// Administrator only, every verb — the same posture <see cref="ConnectionsApi"/>
/// takes for vCenter connections, and for the same two reasons: this names an
/// endpoint and an account an attacker would want, and a wrong setting here is
/// not one operator's report failing to arrive, it is every subscriber's.
/// Managing which reports go out, by contrast, is an operational act — see
/// <see cref="ReportsApi"/>.
/// </remarks>
public static class EmailApi
{
    public static IEndpointRouteBuilder MapEmailApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var email = endpoints
            .MapGroup("/api/email")
            .RequireAuthorization(ObservatoryApi.Policies.Administrator);

        email.MapGet("/settings", (ISmtpSettingsStore store) => Results.Ok(ToView(store.Current)))
            .WithName("GetSmtpSettings");

        email.MapPut("/settings", (ISmtpSettingsStore store, SmtpSettingsCommand command) =>
            {
                if (Parse(command) is not { } settings)
                {
                    return Problem(["The TLS mode was not recognised."]);
                }

                var problems = settings.Validate();

                if (problems.Count > 0)
                {
                    return Problem(problems);
                }

                store.Save(settings);
                return Results.Ok(ToView(store.Current));
            })
            .WithName("UpdateSmtpSettings");

        // Its own verb, like ConnectionsApi's /test: a relay can be tried
        // before it is saved, and the whole point of a "test" button is
        // finding out a password is wrong before anything depends on it.
        email.MapPost("/test", async (
                IMailSender sender,
                ISmtpSettingsStore store,
                TestEmailCommand command,
                CancellationToken cancellationToken) =>
            {
                if (Parse(command) is not { } settings)
                {
                    return Results.Ok(new MailTestView { Succeeded = false, Detail = "The TLS mode was not recognised." });
                }

                if (!MailAddresses.IsValid(command.To))
                {
                    return Results.Ok(new MailTestView { Succeeded = false, Detail = "The recipient address is not valid." });
                }

                // A blank password in the form means "use the stored one",
                // the same rule ConnectionsApi.MapPost("/test") follows: an
                // edit form cannot show the stored password, so testing it
                // unchanged must not be reported as a failure the product
                // would never actually have.
                if (settings.Password.IsEmpty && !store.Current.Password.IsEmpty)
                {
                    settings = settings with { Password = store.Current.Password };
                }

                var problems = settings.Validate();

                if (problems.Count > 0)
                {
                    return Results.Ok(new MailTestView { Succeeded = false, Detail = string.Join(" ", problems) });
                }

                var result = await sender.SendAsync(
                    settings,
                    new OutgoingMail
                    {
                        To = [command.To],
                        Subject = "Enterprise Observatory — test email",
                        BodyText = "This is a test message from Enterprise Observatory's email settings " +
                                   "page. If it arrived, this installation can send scheduled reports.",
                    },
                    cancellationToken).ConfigureAwait(false);

                return Results.Ok(new MailTestView { Succeeded = result.Succeeded, Detail = result.Detail });
            })
            .WithName("TestSmtpSettings");

        return endpoints;
    }

    private static SmtpSettings? Parse(SmtpSettingsCommand command) =>
        Enum.TryParse<SmtpTlsMode>(command.TlsMode, out var tls)
            ? new SmtpSettings
            {
                Host = command.Host?.Trim() ?? string.Empty,
                Port = command.Port,
                TlsMode = tls,
                FromAddress = command.FromAddress?.Trim() ?? string.Empty,
                Username = command.Username?.Trim() ?? string.Empty,
                Password = Secret.From(command.Password),
                AllowUnencrypted = command.AllowUnencrypted,
            }
            : null;

    private static IResult Problem(IReadOnlyList<string> problems) =>
        Results.Problem(title: "Not applied", detail: string.Join(" ", problems), statusCode: StatusCodes.Status400BadRequest);

    private static SmtpSettingsView ToView(SmtpSettings settings) => new()
    {
        Host = settings.Host,
        Port = settings.Port,
        TlsMode = settings.TlsMode.ToString(),
        FromAddress = settings.FromAddress,
        Username = settings.Username,
        AllowUnencrypted = settings.AllowUnencrypted,
        PasswordStatus = settings.Password.IsEmpty ? "not set" : "set",
        PasswordSetUtc = settings.PasswordSetUtc,
        IsConfigured = settings.IsConfigured,
    };
}
