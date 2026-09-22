using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EnterpriseObservatory.Api;

/// <summary>A connection, as the interface shows it.</summary>
/// <remarks>
/// There is no password on this record and there is no endpoint that returns
/// one. That is not an oversight to be corrected when someone asks for a
/// "reveal" button: a credential that can be read back out of the product is a
/// credential that leaks through a screenshot, a support session or an
/// over-broad account. The product can use it; nobody can read it.
/// </remarks>
public sealed record ConnectionView
{
    public required string InstanceId { get; init; }

    public required string Kind { get; init; }

    public required string BaseAddress { get; init; }

    public required string Username { get; init; }

    public required bool AcceptUntrustedCertificate { get; init; }

    public required int PageSize { get; init; }

    public required bool IsEnabled { get; init; }

    /// <summary>"Managed" or "Configuration". A configured one cannot be edited.</summary>
    public required string Origin { get; init; }

    /// <summary>Whether a usable password is stored.</summary>
    public required bool HasPassword { get; init; }

    /// <summary>Whether a password is stored but cannot be decrypted.</summary>
    /// <remarks>
    /// Shown because the remedy is specific and nothing else on the screen
    /// implies it: the database was restored without its key material, and the
    /// passwords have to be entered again.
    /// </remarks>
    public required bool PasswordUnreadable { get; init; }

    public DateTimeOffset? PasswordSetUtc { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required string CreatedBy { get; init; }
}

/// <summary>What the form sends.</summary>
/// <remarks>
/// The password arrives as an ordinary string because that is what JSON has,
/// and becomes a <see cref="Secret"/> at the first opportunity — this record
/// is the only place in the product where a credential exists as a string, and
/// it is one field long.
/// </remarks>
public sealed record ConnectionCommand
{
    public required string InstanceId { get; init; }

    public string Kind { get; init; } = "vsphere";

    public required string BaseAddress { get; init; }

    public required string Username { get; init; }

    /// <summary>Empty when editing means "keep the stored password".</summary>
    public string Password { get; init; } = string.Empty;

    public bool AcceptUntrustedCertificate { get; init; }

    public int PageSize { get; init; } = 250;

    public bool IsEnabled { get; init; } = true;
}

/// <summary>One counter a source says it can supply.</summary>
public sealed record CounterView
{
    public required string Key { get; init; }

    public required string Unit { get; init; }

    /// <summary>The statistics level at which the platform starts collecting it.</summary>
    public required int Level { get; init; }
}

/// <summary>What one test attempt found.</summary>
public sealed record ProbeView
{
    public required bool Succeeded { get; init; }

    public required string Detail { get; init; }

    public string? Identified { get; init; }

    public required bool CredentialsRejected { get; init; }

    /// <summary>Non-null when there is no collector for this kind yet.</summary>
    public bool NoCollector { get; init; }
}

/// <summary>
/// Connections, entered in the product.
/// </summary>
/// <remarks>
/// <para>
/// Administrator only, every verb including the list. A connection record names
/// a management endpoint and the account that reads it, which is most of what
/// an attacker needs to know before they start; it is not operational
/// information and it does not belong on an operator's screen.
/// </para>
/// <para>
/// Testing is a separate verb from saving on purpose. Someone who has just
/// typed a password should be able to find out whether it works before the
/// polling loop starts using it — the alternative is discovering it from an
/// alert four cycles later, which is how the first live run of this product
/// went.
/// </para>
/// </remarks>
public static class ConnectionsApi
{
    public static IEndpointRouteBuilder MapConnections(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var connections = endpoints
            .MapGroup("/api/connections")
            .RequireAuthorization(ObservatoryApi.Policies.Administrator);

        connections.MapGet("/", (SourceConnectionCatalogue catalogue) =>
                catalogue.All().Select(ToView))
            .WithName("GetConnections");

        connections.MapPost("/", (
                HttpContext context,
                SourceConnectionCatalogue catalogue,
                ConnectionCommand command) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                if (Parse(command) is not { } connection)
                {
                    return Problem(ConnectionResult.Rejected(["The address must be an absolute URL."]));
                }

                if (!ConnectionKinds.IsKnown(connection.Kind))
                {
                    return Problem(ConnectionResult.Rejected([UnknownKind(connection.Kind)]));
                }

                return Respond(catalogue.Add(connection, actor.AuditName));
            })
            .WithName("AddConnection");

        connections.MapPut("/{instanceId}", (
                HttpContext context,
                SourceConnectionCatalogue catalogue,
                string instanceId,
                ConnectionCommand command) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                // The name in the path wins. A body that renames the connection
                // would orphan every entity built from the old name, silently,
                // which is a data loss dressed up as an edit.
                if (Parse(command with { InstanceId = instanceId }) is not { } connection)
                {
                    return Problem(ConnectionResult.Rejected(["The address must be an absolute URL."]));
                }

                if (!ConnectionKinds.IsKnown(connection.Kind))
                {
                    return Problem(ConnectionResult.Rejected([UnknownKind(connection.Kind)]));
                }

                return Respond(catalogue.Update(connection, actor.AuditName));
            })
            .WithName("UpdateConnection");

        connections.MapDelete("/{instanceId}", (
                SourceConnectionCatalogue catalogue, string instanceId) =>
                Respond(catalogue.Remove(instanceId)))
            .WithName("RemoveConnection");

        // Takes the form's contents rather than the stored connection, so that
        // a password can be tested before it is saved. Testing only what is
        // already stored would mean saving a wrong credential to find out it is
        // wrong, which is the sequence that locks accounts out.
        connections.MapPost("/test", async (
                SourceConnectionCatalogue catalogue,
                ConnectionCommand command,
                CancellationToken cancellationToken) =>
            {
                if (Parse(command) is not { } connection)
                {
                    return Results.Ok(new ProbeView
                    {
                        Succeeded = false,
                        Detail = "The address must be an absolute URL.",
                        CredentialsRejected = false,
                    });
                }

                if (!ConnectionKinds.IsKnown(connection.Kind))
                {
                    return Results.Ok(new ProbeView
                    {
                        Succeeded = false,
                        Detail = UnknownKind(connection.Kind),
                        CredentialsRejected = false,
                    });
                }

                // An edit form leaves the password blank to mean "unchanged",
                // so a test from that form has to use the stored one or it
                // would report a failure the product would never have had.
                if (connection.Password.IsEmpty &&
                    catalogue.Find(connection.InstanceId) is { } stored &&
                    !stored.Password.IsEmpty)
                {
                    connection = connection with { Password = stored.Password };
                }

                // The catalogue picks the prober by kind, and never throws for a
                // kind that does not have one yet -- see ConnectionFailure.NoCollector.
                var result = await catalogue.ProbeAsync(connection, cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new ProbeView
                {
                    Succeeded = result.Succeeded,
                    Detail = result.Detail,
                    Identified = result.Identified,
                    CredentialsRejected = result.CredentialsRejected,
                    NoCollector = result.Failure == ConnectionFailure.NoCollector,
                });
            })
            .WithName("TestConnection");

        // What this source can actually supply. The question an operator has
        // the moment they see "counter is not defined", and the one the
        // product could not answer from inside itself — which is how two
        // counter names that do not exist survived months of development.
        connections.MapGet("/{instanceId}/counters", async (
                ISourceCapabilityReader reader,
                SourceConnectionCatalogue catalogue,
                string instanceId,
                string? prefix,
                CancellationToken cancellationToken) =>
            {
                if (catalogue.Find(instanceId) is not { } connection)
                {
                    return Results.NotFound();
                }

                var counters = await reader
                    .CountersAsync(connection, cancellationToken)
                    .ConfigureAwait(false);

                // A live vCenter defines several hundred, which is a useless
                // wall of text and a large response. The filter is what makes
                // it answerable: "what datastore counters exist here".
                return Results.Ok(counters
                    .Where(c => string.IsNullOrEmpty(prefix) ||
                                c.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new CounterView { Key = c.Key, Unit = c.Unit, Level = c.Level }));
            })
            .WithName("GetConnectionCounters");

        return endpoints;
    }

    /// <summary>Turns a command into a connection, or null if the address is not one.</summary>
    private static SourceConnection? Parse(ConnectionCommand command) =>
        Uri.TryCreate(command.BaseAddress, UriKind.Absolute, out var address)
            ? new SourceConnection
            {
                InstanceId = command.InstanceId?.Trim() ?? string.Empty,
                Kind = command.Kind,
                BaseAddress = address,
                Username = command.Username?.Trim() ?? string.Empty,
                Password = Secret.From(command.Password),
                AcceptUntrustedCertificate = command.AcceptUntrustedCertificate,
                PageSize = command.PageSize,
                IsEnabled = command.IsEnabled,
            }
            : null;

    private static IResult Respond(ConnectionResult result) =>
        result.Applied ? Results.Ok(ToView(result.Connection!)) : Problem(result);

    private static IResult Problem(ConnectionResult result) =>
        Results.Problem(
            title: "Not applied",
            detail: Explain(result),
            statusCode: result.Failure switch
            {
                ConnectionFailure.NotFound => StatusCodes.Status404NotFound,
                ConnectionFailure.NameTaken => StatusCodes.Status409Conflict,
                ConnectionFailure.ReadOnly => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            });

    private static string Explain(ConnectionResult result) => result.Failure switch
    {
        ConnectionFailure.NotFound => "No connection has that name.",
        ConnectionFailure.NameTaken => "A connection with that name already exists.",
        ConnectionFailure.ReadOnly =>
            "This connection comes from configuration and is owned by whoever deploys the " +
            "service. Change it there — an edit here would be undone by the next restart.",
        ConnectionFailure.Invalid => string.Join(" ", result.Problems),
        _ => "The change was not applied.",
    };

    private static string UnknownKind(string kind) =>
        $"'{kind}' is not a connection kind this product knows. It must be one of: " +
        string.Join(", ", ConnectionKinds.All) + ".";

    private static ConnectionView ToView(SourceConnection connection) => new()
    {
        InstanceId = connection.InstanceId,
        Kind = connection.Kind,
        BaseAddress = connection.BaseAddress.ToString(),
        Username = connection.Username,
        AcceptUntrustedCertificate = connection.AcceptUntrustedCertificate,
        PageSize = connection.PageSize,
        IsEnabled = connection.IsEnabled,
        Origin = connection.Origin.ToString(),
        HasPassword = !connection.Password.IsEmpty,
        PasswordUnreadable = connection.PasswordUnreadable,
        PasswordSetUtc = connection.PasswordSetUtc,
        CreatedUtc = connection.CreatedUtc,
        CreatedBy = connection.CreatedBy,
    };
}
