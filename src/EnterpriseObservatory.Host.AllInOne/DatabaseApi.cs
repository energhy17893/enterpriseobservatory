using EnterpriseObservatory.Api;
using EnterpriseObservatory.Host.AllInOne.Configuration;
using EnterpriseObservatory.Persistence.Postgres;
using Npgsql;

namespace EnterpriseObservatory.Host.AllInOne;

/// <summary>The database connection, as the Database card shows it.</summary>
/// <remarks>
/// No password, and no endpoint returns one — the same rule as
/// <see cref="ConnectionView"/>. The product can use it; nobody can read it.
/// </remarks>
public sealed record DatabaseView
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required string Database { get; init; }

    public required string Username { get; init; }

    public required string Schema { get; init; }

    public required bool RequireTls { get; init; }

    /// <summary>"ProtectedFile" or "Configuration".</summary>
    public required string Source { get; init; }

    public required bool CanRotate { get; init; }

    /// <summary>Why rotation is not offered here, when it is not.</summary>
    public string? RotateRefusal { get; init; }

    public DateTimeOffset? PasswordSetUtc { get; init; }
}

/// <summary>What a test or a rotation found.</summary>
public sealed record DatabaseActionView
{
    public required bool Succeeded { get; init; }

    public required string Detail { get; init; }
}

/// <summary>The connection the running service uses, and where it came from.</summary>
public sealed class DatabaseConnectionState
{
    public DatabaseConnectionState(DatabaseSourceKind source, PostgresOptions options, DatabaseCredentialFile? file)
    {
        ArgumentNullException.ThrowIfNull(options);

        Source = source;
        Options = options;
        File = file;
    }

    public DatabaseSourceKind Source { get; }

    /// <summary>The protected file, when that is where the connection came from.</summary>
    public DatabaseCredentialFile? File { get; }

    /// <summary>The connection as it stands now; replaced whole on rotation.</summary>
    public PostgresOptions Options { get; private set; }

    public Lock Gate { get; } = new();

    // Called under Gate, by the one rotation that holds it.
    internal void Replace(PostgresOptions options) => Options = options;
}

/// <summary>
/// The Database card on the Connections screen: what the product is connected
/// to, whether it works, and a new password for it.
/// </summary>
/// <remarks>
/// <para>
/// Administrator only, every verb, under ADR-0014's ordinary rules — this is
/// outside setup mode, so there is a session to require. Like the connection
/// list, it names infrastructure an attacker would want to know about.
/// </para>
/// <para>
/// Rotation needs no administrator credential: the product's role changes its
/// own password (<c>ALTER ROLE CURRENT_USER</c>). It is offered only when the
/// connection came from the protected file. A password from user secrets or
/// an environment variable lives where the product cannot write, and changing
/// it on the server alone would lock the product out at the next restart.
/// </para>
/// </remarks>
public static class DatabaseApi
{
    public const string ConfigurationRefusal =
        "The database password comes from configuration (user secrets or an environment variable). " +
        "Rotate it there and on the server, then restart the service.";

    public static IEndpointRouteBuilder MapDatabaseApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var database = endpoints
            .MapGroup("/api/database")
            .RequireAuthorization(ObservatoryApi.Policies.Administrator);

        database.MapGet("/", (DatabaseConnectionState state) => Results.Ok(ToView(state)))
            .WithName("GetDatabase");

        // Through the product's own pool, not a fresh connection: the question
        // is whether the service can reach its database, not whether some
        // other connection could.
        database.MapPost("/test", (IServiceProvider services, DatabaseConnectionState state) =>
            {
                var options = state.Options;

                try
                {
                    var (user, superuser) = services.GetRequiredService<PostgresDatabase>().Read(connection =>
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText =
                            "SELECT current_user, rolsuper FROM pg_roles WHERE rolname = current_user;";
                        using var reader = command.ExecuteReader();
                        reader.Read();
                        return (reader.GetString(0), reader.GetBoolean(1));
                    });

                    return Results.Ok(new DatabaseActionView
                    {
                        Succeeded = !superuser,
                        Detail = superuser
                            ? $"Connected as '{user}', which is a superuser. The product must not run as one; " +
                              "give it a role of its own."
                            : $"Connected to {options.Host}:{options.Port}/{options.Database} as '{user}'.",
                    });
                }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
                {
                    return Results.Ok(new DatabaseActionView
                    {
                        Succeeded = false,
                        Detail = PostgresProvisioning.Explain(ex, options, options.Password),
                    });
                }
            })
            .WithName("TestDatabase");

        database.MapPost("/rotate", (
                HttpContext context,
                IServiceProvider services,
                DatabaseConnectionState state,
                ILoggerFactory loggers) =>
            {
                if (AuthenticationApi.OperatorFor(context) is not { } actor)
                {
                    return Results.Unauthorized();
                }

                if (state.Source is not DatabaseSourceKind.ProtectedFile || state.File is not { } file)
                {
                    return Results.Conflict(new DatabaseActionView { Succeeded = false, Detail = ConfigurationRefusal });
                }

                var logger = loggers.CreateLogger("EnterpriseObservatory.Database");
                var result = Rotate(state, file, services.GetRequiredService<PostgresDatabase>());

                if (result.Succeeded)
                {
                    HostLog.DatabasePasswordRotated(logger, actor.AuditName);
                    return Results.Ok(result);
                }

                HostLog.DatabasePasswordRotationFailed(logger, actor.AuditName, result.Detail);
                return Results.Ok(result);
            })
            .WithName("RotateDatabasePassword");

        return endpoints;
    }

    /// <summary>
    /// A new password: staged on disk, set on the server, moved into place,
    /// then proved with a fresh connection.
    /// </summary>
    /// <remarks>
    /// In that order so no step can leave the product holding a password the
    /// server does not accept. If the server refuses, the staged file is
    /// discarded and the old one is untouched. The one window left — the
    /// process dying between the server change and the move — leaves the new
    /// password in <c>database-connection.json.pending</c>, which is named in
    /// ADR-0010's addendum.
    /// </remarks>
    private static DatabaseActionView Rotate(
        DatabaseConnectionState state, DatabaseCredentialFile file, PostgresDatabase database)
    {
        lock (state.Gate)
        {
            var current = state.Options;
            var next = current with { Password = PostgresProvisioning.NewPassword() };
            string staged;

            try
            {
                staged = file.Stage(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new DatabaseActionView
                {
                    Succeeded = false,
                    Detail = $"The new password could not be written beside {file.Path} ({ex.GetType().Name}); " +
                             "nothing was changed.",
                };
            }

            try
            {
                PostgresProvisioning.RotatePassword(database, next.Password);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                DatabaseCredentialFile.Discard(staged);

                return new DatabaseActionView
                {
                    Succeeded = false,
                    Detail = PostgresProvisioning.Explain(ex, current, current.Password, next.Password) +
                             " The password was not changed.",
                };
            }

            file.Commit(staged);
            state.Replace(next);

            var proved = PostgresProvisioning.CanConnect(file.Read());

            return new DatabaseActionView
            {
                Succeeded = proved.Succeeded,
                Detail = proved.Succeeded
                    ? "The password was changed, saved and verified with a new connection."
                    : "The password was changed and saved, but a new connection with it failed: " + proved.Detail,
            };
        }
    }

    private static DatabaseView ToView(DatabaseConnectionState state)
    {
        var options = state.Options;
        var fromFile = state.Source is DatabaseSourceKind.ProtectedFile;

        return new DatabaseView
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Database,
            Username = options.Username,
            Schema = options.Schema,
            RequireTls = options.RequireTls,
            Source = state.Source.ToString(),
            CanRotate = fromFile,
            RotateRefusal = fromFile ? null : ConfigurationRefusal,
            PasswordSetUtc = fromFile ? state.File?.PasswordSetUtc : null,
        };
    }
}
