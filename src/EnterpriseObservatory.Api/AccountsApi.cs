using EnterpriseObservatory.Application.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EnterpriseObservatory.Api;

/// <summary>An account, as the interface lists it.</summary>
/// <remarks>
/// No verifier, not even a redacted one. There is nothing a client can do with
/// it, and a field that exists is a field that eventually gets logged.
/// </remarks>
public sealed record AccountView
{
    public required string Username { get; init; }

    public required Role Role { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? LastSignedInUtc { get; init; }

    /// <summary>Whether the account is currently refusing sign-in attempts.</summary>
    public required bool LockedOut { get; init; }
}

public sealed record CreateAccountCommand
{
    public required string Username { get; init; }

    public required string Password { get; init; }

    public required Role Role { get; init; }
}

public sealed record ChangeOwnPasswordCommand
{
    public required string CurrentPassword { get; init; }

    public required string NewPassword { get; init; }
}

public sealed record ResetPasswordCommand
{
    public required string Username { get; init; }

    public required string NewPassword { get; init; }
}

public sealed record ChangeRoleCommand
{
    public required string Username { get; init; }

    public required Role Role { get; init; }
}

public sealed record RemoveAccountCommand
{
    public required string Username { get; init; }
}

/// <summary>
/// Managing accounts.
/// </summary>
/// <remarks>
/// <para>
/// Administrator-only, with one exception: anybody may change their own
/// password. That exception is the point — a product where only an
/// administrator can rotate a credential is a product where nobody rotates
/// credentials.
/// </para>
/// <para>
/// See ADR-0014.
/// </para>
/// </remarks>
public static class AccountsApi
{
    public static IEndpointRouteBuilder MapAccountsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accounts = endpoints.MapGroup("/api/accounts").RequireAuthorization();

        // Anyone signed in, about themselves only. The username comes from the
        // session rather than the body, so this cannot be pointed at somebody
        // else by editing a request.
        accounts.MapPost("/password", (
                HttpContext context,
                AccountService service,
                ChangeOwnPasswordCommand command) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                return Respond(service.ChangeOwnPassword(
                    actor.Name,
                    Secret.From(command.CurrentPassword),
                    Secret.From(command.NewPassword)));
            })
            .WithName("ChangeOwnPassword");

        var admin = accounts.MapGroup(string.Empty)
            .RequireAuthorization(ObservatoryApi.Policies.Administrator);

        admin.MapGet("/", (AccountService service) =>
                service.All().Select(a => ToView(a, DateTimeOffset.UtcNow)))
            .WithName("GetAccounts");

        admin.MapPost("/create", (AccountService service, CreateAccountCommand command) =>
                Respond(service.Create(
                    command.Username, Secret.From(command.Password), command.Role)))
            .WithName("CreateAccount");

        admin.MapPost("/reset-password", (AccountService service, ResetPasswordCommand command) =>
                Respond(service.ResetPassword(command.Username, Secret.From(command.NewPassword))))
            .WithName("ResetPassword");

        admin.MapPost("/role", (AccountService service, ChangeRoleCommand command) =>
                Respond(service.ChangeRole(command.Username, command.Role)))
            .WithName("ChangeRole");

        admin.MapPost("/remove", (AccountService service, RemoveAccountCommand command) =>
                Respond(service.Remove(command.Username)))
            .WithName("RemoveAccount");

        return endpoints;
    }

    private static IResult Respond(AccountChangeResult result) =>
        result.Applied
            ? Results.Ok(ToView(result.Account!, DateTimeOffset.UtcNow))
            : Results.Problem(
                title: "Not applied",
                detail: Explain(result.Failure),
                statusCode: result.Failure switch
                {
                    AccountChangeFailure.NotFound => StatusCodes.Status404NotFound,
                    AccountChangeFailure.NameTaken => StatusCodes.Status409Conflict,
                    AccountChangeFailure.WouldLockEverybodyOut => StatusCodes.Status409Conflict,
                    AccountChangeFailure.WrongPassword => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status400BadRequest,
                });

    private static AccountView ToView(UserAccount account, DateTimeOffset nowUtc) => new()
    {
        Username = account.Username,
        Role = account.Role,
        CreatedUtc = account.CreatedUtc,
        LastSignedInUtc = account.LastSignedInUtc,
        LockedOut = account.IsLockedAt(nowUtc),
    };

    private static string Explain(AccountChangeFailure failure) => failure switch
    {
        AccountChangeFailure.NotFound => "There is no account by that name.",
        AccountChangeFailure.NameTaken => "That username is already taken.",
        AccountChangeFailure.WrongPassword => "The current password was not accepted.",
        AccountChangeFailure.WouldLockEverybodyOut =>
            "That would leave this installation with no administrator. Promote somebody else " +
            "first. An installation nobody can administer has to be repaired by editing the " +
            "database, and whoever does that will be doing it during an incident.",
        AccountChangeFailure.Unusable =>
            $"A username is required, and a password must be at least " +
            $"{AuthenticationService.MinimumPasswordLength} characters.",
        _ => "The change was not applied.",
    };
}
