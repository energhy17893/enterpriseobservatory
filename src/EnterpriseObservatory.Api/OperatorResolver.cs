using EnterpriseObservatory.Application.Security;
using Microsoft.AspNetCore.Http;

namespace EnterpriseObservatory.Api;

/// <summary>What the product is prepared to let an operator change.</summary>
public sealed record OperationsOptions
{
    /// <summary>
    /// Whether an unauthenticated caller may change alert state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default and never implicit. Authentication is not built yet
    /// (ADR-0006 chose the mechanism, not the user store), and until it is,
    /// anyone who can reach the port could acknowledge or clear alerts. On a
    /// management network that may be an acceptable trade for a small team,
    /// and on a routed one it is not — so it is a decision somebody records,
    /// not something the product does quietly on their behalf.
    /// </para>
    /// <para>
    /// The same shape as accepting an untrusted certificate, for the same
    /// reason.
    /// </para>
    /// </remarks>
    public bool AllowUnauthenticatedWrites { get; init; }

    public static OperationsOptions Default { get; } = new();
}

/// <summary>Works out who is making a request.</summary>
public static class OperatorResolver
{
    /// <summary>
    /// The operator behind a request, or null when the product will not accept
    /// one.
    /// </summary>
    /// <remarks>
    /// Never invents a name. An unauthenticated caller is identified by where
    /// the request came from and recorded as unverified, so that a history read
    /// years later does not have to know how the installation was configured at
    /// the time. See <see cref="OperatorIdentity"/>.
    /// </remarks>
    public static OperatorIdentity? Resolve(HttpContext context, OperationsOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name })
        {
            return OperatorIdentity.Verified(name);
        }

        if (!options.AllowUnauthenticatedWrites)
        {
            return null;
        }

        return OperatorIdentity.Unverified(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// <summary>What to tell a caller the product will not accept.</summary>
    /// <remarks>
    /// Says why and how to change it. A bare 403 on a button that exists sends
    /// the operator to the logs, and there is nothing there either.
    /// </remarks>
    public static IResult Refused() => Results.Problem(
        title: "Not signed in",
        detail: "Changing an alert has to be attributable to someone. This installation has no " +
                "authentication configured, and unauthenticated changes are not enabled. Set " +
                "Operations:AllowUnauthenticatedWrites to true to accept them anyway — they will " +
                "be recorded as unverified, naming the address they came from.",
        statusCode: StatusCodes.Status403Forbidden);
}
