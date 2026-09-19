using System.Security.Claims;
using EnterpriseObservatory.Application.Security;
using Accounts = EnterpriseObservatory.Application.Security.AuthenticationService;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EnterpriseObservatory.Api;

/// <summary>What the caller sends to sign in.</summary>
public sealed record SignInCommand
{
    public required string Username { get; init; }

    public required string Password { get; init; }
}

/// <summary>What the caller sends to create the first account.</summary>
public sealed record BootstrapCommand
{
    /// <summary>The setup token this run of the service printed.</summary>
    public required string Token { get; init; }

    public required string Username { get; init; }

    public required string Password { get; init; }
}

/// <summary>Who the caller is, and what this installation needs.</summary>
public sealed record AuthStateView
{
    /// <summary>Whether anybody is signed in on this request.</summary>
    public required bool SignedIn { get; init; }

    public string? Username { get; init; }

    public Role? Role { get; init; }

    /// <summary>
    /// Whether the product still has no accounts at all.
    /// </summary>
    /// <remarks>
    /// Told to an unauthenticated caller on purpose. It is only true before
    /// anyone has claimed the installation, and the alternative is a login form
    /// that rejects every credential with no explanation of why.
    /// </remarks>
    public required bool NeedsSetup { get; init; }

    /// <summary>The shortest password the product will accept.</summary>
    public required int MinimumPasswordLength { get; init; }
}

/// <summary>
/// Signing in, signing out, and claiming a new installation.
/// </summary>
/// <remarks>
/// <para>
/// A cookie, not a token in browser storage. The interface and the API are
/// served from one origin precisely so this works (ADR-0006): the cookie is
/// HttpOnly, so a script that gets injected into the page cannot read it, and
/// SameSite, so another site cannot make the browser send it.
/// </para>
/// <para>
/// See ADR-0014.
/// </para>
/// </remarks>
public static class AuthenticationApi
{
    /// <summary>The scheme name, shared by the host's cookie configuration.</summary>
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>The claim carrying the account's role.</summary>
    public const string RoleClaim = ClaimTypes.Role;

    public static IEndpointRouteBuilder MapAuthenticationApi(
        this IEndpointRouteBuilder endpoints, string setupToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var auth = endpoints.MapGroup("/api/auth").AllowAnonymous();

        auth.MapGet("/state", (HttpContext context, Accounts authentication) =>
                new AuthStateView
                {
                    SignedIn = context.User.Identity?.IsAuthenticated == true,
                    Username = context.User.Identity?.Name,
                    Role = RoleOf(context.User),
                    NeedsSetup = authentication.NeedsBootstrap,
                    MinimumPasswordLength = Accounts.MinimumPasswordLength,
                })
            .WithName("GetAuthState");

        auth.MapPost("/signin", async (
                HttpContext context,
                Accounts authentication,
                SignInCommand command) =>
            {
                var result = authentication.SignIn(command.Username, Secret.From(command.Password));

                if (!result.Succeeded)
                {
                    // One message for a wrong username and a wrong password.
                    // Telling them apart turns the login form into a way of
                    // finding out who has an account here.
                    return result.Failure == SignInFailure.LockedOut
                        ? Results.Problem(
                            title: "Temporarily locked",
                            detail: "Too many failed attempts. Try again in a few minutes.",
                            statusCode: StatusCodes.Status429TooManyRequests)
                        : Results.Problem(
                            title: "Not accepted",
                            detail: "That username and password combination was not accepted.",
                            statusCode: StatusCodes.Status401Unauthorized);
                }

                await SignInAsync(context, result.Account!).ConfigureAwait(false);

                return Results.Ok(new AuthStateView
                {
                    SignedIn = true,
                    Username = result.Account!.Username,
                    Role = result.Account.Role,
                    NeedsSetup = false,
                    MinimumPasswordLength = Accounts.MinimumPasswordLength,
                });
            })
            .WithName("SignIn");

        auth.MapPost("/signout", async (HttpContext context) =>
            {
                await context.SignOutAsync(Scheme).ConfigureAwait(false);
                return Results.NoContent();
            })
            .WithName("SignOut");

        auth.MapPost("/bootstrap", async (
                HttpContext context,
                Accounts authentication,
                BootstrapCommand command) =>
            {
                var failure = authentication.CreateFirstAccount(
                    setupToken, command.Token, command.Username, Secret.From(command.Password));

                if (failure != BootstrapFailure.None)
                {
                    return Results.Problem(
                        title: "Setup refused",
                        detail: Explain(failure),
                        statusCode: failure == BootstrapFailure.AlreadyConfigured
                            ? StatusCodes.Status409Conflict
                            : StatusCodes.Status400BadRequest);
                }

                // Signed in straight away: having just proved they hold the
                // setup token and chosen the password, asking them to type it
                // again proves nothing.
                var account = new UserAccount
                {
                    Username = UserAccount.Normalize(command.Username),
                    Password = default,
                    Role = Role.Administrator,
                    CreatedUtc = DateTimeOffset.UtcNow,
                };

                await SignInAsync(context, account).ConfigureAwait(false);

                return Results.Ok(new AuthStateView
                {
                    SignedIn = true,
                    Username = account.Username,
                    Role = account.Role,
                    NeedsSetup = false,
                    MinimumPasswordLength = Accounts.MinimumPasswordLength,
                });
            })
            .WithName("Bootstrap");

        return endpoints;
    }

    /// <summary>The role on a signed-in principal, or null.</summary>
    public static Role? RoleOf(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var claim = principal.FindFirst(RoleClaim)?.Value;

        return Enum.TryParse<Role>(claim, out var role) ? role : null;
    }

    /// <summary>The operator behind a request, for the audit trail.</summary>
    /// <remarks>
    /// Always verified now: an unauthenticated request never reaches a handler.
    /// The unverified case remains expressible because the audit trail contains
    /// entries written before authentication existed, and those must keep
    /// reading as what they were.
    /// </remarks>
    public static OperatorIdentity? OperatorFor(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }
            ? OperatorIdentity.Verified(name)
            : null;
    }

    private static Task SignInAsync(HttpContext context, UserAccount account)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, account.Username),
                new Claim(RoleClaim, account.Role.ToString()),
            ],
            Scheme);

        return context.SignInAsync(Scheme, new ClaimsPrincipal(identity));
    }

    private static string Explain(BootstrapFailure failure) => failure switch
    {
        BootstrapFailure.AlreadyConfigured =>
            "This installation already has an account. Sign in, or ask an administrator to add you.",
        BootstrapFailure.BadToken =>
            "That setup token is not the one this service printed when it started. It is written to " +
            "the service log, and restarting the service issues a new one.",
        BootstrapFailure.Unusable =>
            $"A username is required, and a password must be at least " +
            $"{Accounts.MinimumPasswordLength} characters.",
        _ => "Setup was refused.",
    };
}
