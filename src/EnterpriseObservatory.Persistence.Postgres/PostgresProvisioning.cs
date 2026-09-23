using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EnterpriseObservatory.Application.Security;
using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// A database administrator's credential, entered once on the setup screen.
/// </summary>
/// <remarks>
/// Lives for exactly one request. Nothing in the product stores, logs or
/// returns it: it is used to create the product's own database and role and is
/// then dropped with the request. The product itself never connects as this
/// account — or as any superuser — afterwards.
/// </remarks>
public sealed record PostgresAdminCredential
{
    public required string Username { get; init; }

    public required Secret Password { get; init; }

    /// <summary>The database the administrator connects to first.</summary>
    public string MaintenanceDatabase { get; init; } = "postgres";
}

/// <summary>What a provisioning step did.</summary>
/// <param name="Succeeded">Whether it did what it was asked.</param>
/// <param name="Detail">
/// A sentence for the operator. Built from fixed text and PostgreSQL's own
/// message, and checked against every secret the step held before it is
/// returned: it never carries a password.
/// </param>
public sealed record ProvisioningResult(bool Succeeded, string Detail)
{
    public static ProvisioningResult Ok(string detail) => new(true, detail);

    public static ProvisioningResult Failed(string detail) => new(false, detail);
}

/// <summary>
/// The statements that create the product's database and role, in order, run
/// as the administrator against the maintenance database.
/// </summary>
public sealed record ProvisioningPlan(IReadOnlyList<string> Statements);

/// <summary>
/// Creates, checks and rotates the product's own PostgreSQL role and database.
/// </summary>
/// <remarks>
/// <para>
/// The first-run setup screen's back end (G-DB), and deliberately the only
/// code in the product that ever holds a database administrator's credential.
/// ADR-0016 accepted a two-step install — provision PostgreSQL, then point the
/// product at it — and this brings the second step into the product, the way
/// pgAdmin and Grafana do: one page, once.
/// </para>
/// <para>
/// The role it creates is never a superuser and cannot create databases or
/// roles. Its password is generated here, is never shown to anyone, and is
/// written only to the protected file beside the key ring.
/// </para>
/// </remarks>
public static partial class PostgresProvisioning
{
    /// <summary>
    /// A new password for the product's role: 32 random bytes, base64url.
    /// </summary>
    /// <remarks>
    /// Nobody types it and nobody reads it, so it can be as long as the key
    /// material it is compared with. The alphabet is also what makes it safe to
    /// place in <c>CREATE ROLE ... PASSWORD</c>, which takes no bind parameters;
    /// <see cref="PasswordLiteral"/> refuses anything outside it.
    /// </remarks>
    public static Secret NewPassword() =>
        Secret.From(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));

    /// <summary>What is wrong with the names on the setup form, or empty.</summary>
    public static IReadOnlyList<string> Problems(PostgresOptions target, PostgresAdminCredential admin)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(admin);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(target.Host))
        {
            problems.Add("A host is required.");
        }

        if (target.Port is < 1 or > 65535)
        {
            problems.Add("The port must be between 1 and 65535.");
        }

        CheckName(problems, "database", target.Database);
        CheckName(problems, "role", target.Username);
        CheckName(problems, "schema", target.Schema);

        if (string.IsNullOrWhiteSpace(admin.Username))
        {
            problems.Add("The administrator's username is required.");
        }

        if (admin.Password.IsEmpty)
        {
            problems.Add("The administrator's password is required.");
        }

        if (string.Equals(target.Username, "postgres", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Username, admin.Username, StringComparison.Ordinal))
        {
            // The standing rule: the product never connects as postgres, nor as
            // the account that provisioned it.
            problems.Add(
                "The product's role must be its own account, not 'postgres' and not the administrator " +
                "entered below.");
        }

        return problems;
    }

    /// <summary>The statements, in order, for creating <paramref name="target"/>.</summary>
    /// <param name="target">The product's role and database.</param>
    /// <param name="serverMajorVersion">
    /// The server's major version; <c>WITH SET TRUE</c> exists from 16 on.
    /// </param>
    /// <remarks>
    /// <para>
    /// The role first, because the database is created owned by it — and owned
    /// matters: since PostgreSQL 15 the <c>public</c> schema no longer lets
    /// everyone create in it, and the database owner is who it does let. So the
    /// product needs no further grant, and the schema it names is created by
    /// the product itself on its first connection.
    /// </para>
    /// <para>
    /// The administrator need not be a superuser; CREATEDB and CREATEROLE are
    /// enough. From PostgreSQL 16 a CREATEROLE account that creates a role gets
    /// ADMIN OPTION on it but not SET, and <c>CREATE DATABASE ... OWNER</c>
    /// requires SET. So the administrator grants itself SET on the new role for
    /// exactly one statement and revokes it again, keeping no lasting
    /// membership of the product's role.
    /// </para>
    /// <para>
    /// Every identifier is validated and double-quoted. The password is a
    /// literal only because PostgreSQL's DDL takes no parameters, and it is
    /// escaped and restricted to the generated alphabet before it gets there.
    /// </para>
    /// </remarks>
    public static ProvisioningPlan Plan(PostgresOptions target, int serverMajorVersion = 16)
    {
        ArgumentNullException.ThrowIfNull(target);

        var role = Identifier(target.Username);
        var database = Identifier(target.Database);

        return new ProvisioningPlan(
            [
                $"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS " +
                $"PASSWORD {PasswordLiteral(target.Password)};",
                GrantSetToSelf(role, serverMajorVersion),
                $"CREATE DATABASE {database} OWNER {role};",
                $"REVOKE {role} FROM CURRENT_USER;",
            ]);
    }

    private static string GrantSetToSelf(string role, int serverMajorVersion) =>
        serverMajorVersion >= 16
            ? $"GRANT {role} TO CURRENT_USER WITH SET TRUE;"
            : $"GRANT {role} TO CURRENT_USER;";

    /// <summary>
    /// Creates the role and database, then applies the schema as the new role.
    /// </summary>
    /// <remarks>
    /// Refuses before creating anything when either name is already taken, so
    /// that what it undoes on failure is only ever what it created. The schema
    /// is applied through <see cref="PostgresDatabase"/>, the same path every
    /// start takes, which also proves the new role can sign in.
    /// </remarks>
    public static ProvisioningResult Provision(PostgresAdminCredential admin, PostgresOptions target)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(target);

        var problems = Problems(target, admin);

        if (problems.Count > 0)
        {
            return ProvisioningResult.Failed(string.Join(" ", problems));
        }

        var createdRole = false;
        var createdDatabase = false;

        try
        {
            using (var server = OpenAsAdmin(admin, target, admin.MaintenanceDatabase))
            {
                if (Exists(server, "SELECT 1 FROM pg_roles WHERE rolname = @name", target.Username))
                {
                    return ProvisioningResult.Failed(
                        $"A role named '{target.Username}' already exists. Choose another name, or remove it first.");
                }

                if (Exists(server, "SELECT 1 FROM pg_database WHERE datname = @name", target.Database))
                {
                    return ProvisioningResult.Failed(
                        $"A database named '{target.Database}' already exists. Choose another name, or remove it first.");
                }

                var plan = Plan(target, server.PostgreSqlVersion.Major);

                // Statement 0 creates the role, 2 creates the database; what
                // exists after a failure decides what Undo removes.
                for (var i = 0; i < plan.Statements.Count; i++)
                {
                    Execute(server, plan.Statements[i]);
                    createdRole |= i == 0;
                    createdDatabase |= i == 2;
                }
            }

            // As the product's own role, through the product's own path.
            using (new PostgresDatabase(target))
            {
            }

            return ProvisioningResult.Ok(
                $"Created role '{target.Username}' and database '{target.Database}' on " +
                $"{target.Host}:{target.Port}, and applied the schema.");
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            var detail = Explain(ex, target, admin.Password, target.Password);

            if (createdRole || createdDatabase)
            {
                Undo(admin, target, createdDatabase, createdRole);
            }

            return ProvisioningResult.Failed(detail);
        }
    }

    /// <summary>What is wrong with an existing database's details, or empty.</summary>
    public static IReadOnlyList<string> ProblemsForExisting(PostgresOptions target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(target.Host))
        {
            problems.Add("A host is required.");
        }

        if (target.Port is < 1 or > 65535)
        {
            problems.Add("The port must be between 1 and 65535.");
        }

        CheckName(problems, "database", target.Database);
        CheckName(problems, "role", target.Username);
        CheckName(problems, "schema", target.Schema);

        if (target.Password.IsEmpty)
        {
            problems.Add("The role's password is required.");
        }

        if (string.Equals(target.Username, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("The product's role must be its own account, not 'postgres'.");
        }

        return problems;
    }

    /// <summary>
    /// Takes over a database and role a DBA already made: checks the role is
    /// fit to be the product's, then applies the schema as it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path for the estate where a DBA provisions databases and will never
    /// hand the product an administrator's credential — without it, those
    /// customers could not install at all.
    /// </para>
    /// <para>
    /// Refuses a role with SUPERUSER, CREATEROLE or CREATEDB: the product never
    /// runs with any of them (ADR-0016 §4). And checks, before trying, that the
    /// role can create what the schema needs — since PostgreSQL 15 the
    /// <c>public</c> schema no longer grants CREATE to everyone, so a role that
    /// does not own its database fails on the first table. That refusal is a
    /// sentence the operator can hand to the DBA, not a driver error.
    /// </para>
    /// </remarks>
    public static ProvisioningResult Adopt(PostgresOptions target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var problems = ProblemsForExisting(target);

        if (problems.Count > 0)
        {
            return ProvisioningResult.Failed(string.Join(" ", problems));
        }

        try
        {
            using (var connection = new NpgsqlConnection(
                       Unpooled(target, target.Username, target.Password, target.Database)))
            {
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT r.rolsuper, r.rolcreaterole, r.rolcreatedb, " +
                    "       CASE WHEN n.oid IS NULL " +
                    "            THEN has_database_privilege(current_database(), 'CREATE') " +
                    "            ELSE has_schema_privilege(n.oid, 'CREATE') END " +
                    "FROM pg_roles r LEFT JOIN pg_namespace n ON n.nspname = @schema " +
                    "WHERE r.rolname = current_user;";
                command.Parameters.AddWithValue("schema", target.Schema);

                using var reader = command.ExecuteReader();

                if (!reader.Read())
                {
                    return ProvisioningResult.Failed($"The role '{target.Username}' could not be read back from the server.");
                }

                var granted = new List<string>();

                if (reader.GetBoolean(0))
                {
                    granted.Add("SUPERUSER");
                }

                if (reader.GetBoolean(1))
                {
                    granted.Add("CREATEROLE");
                }

                if (reader.GetBoolean(2))
                {
                    granted.Add("CREATEDB");
                }

                if (granted.Count > 0)
                {
                    return ProvisioningResult.Failed(
                        $"The role '{target.Username}' has {string.Join(", ", granted)}. The product never runs " +
                        "with any of these; give it a role of its own without them (ALTER ROLE ... NOSUPERUSER " +
                        "NOCREATEROLE NOCREATEDB), or let setup create one.");
                }

                if (!reader.GetBoolean(3))
                {
                    return ProvisioningResult.Failed(
                        $"The role '{target.Username}' cannot create tables in schema {target.Schema} of database " +
                        $"'{target.Database}'. Ask your DBA to make it the database owner " +
                        $"(ALTER DATABASE {Identifier(target.Database)} OWNER TO {Identifier(target.Username)}) or to " +
                        $"GRANT CREATE ON SCHEMA {Identifier(target.Schema)} TO {Identifier(target.Username)}.");
                }
            }

            // As the product's own role, through the product's own path.
            using (new PostgresDatabase(target))
            {
            }

            return ProvisioningResult.Ok(
                $"Connected to database '{target.Database}' on {target.Host}:{target.Port} as '{target.Username}' " +
                "and applied the schema.");
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            return ProvisioningResult.Failed(Explain(ex, target, target.Password));
        }
    }

    /// <summary>
    /// Removes a database and role this product created.
    /// </summary>
    /// <remarks>
    /// Used when setup fails after creating them, and by the tests' cleanup.
    /// Never called on names setup did not create: <see cref="Provision"/>
    /// refuses names that already exist.
    /// </remarks>
    public static ProvisioningResult Drop(PostgresAdminCredential admin, PostgresOptions target)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(target);

        try
        {
            Undo(admin, target, dropDatabase: true, dropRole: true, quiet: false);
            return ProvisioningResult.Ok($"Removed database '{target.Database}' and role '{target.Username}'.");
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return ProvisioningResult.Failed(Explain(ex, target, admin.Password, target.Password));
        }
    }

    /// <summary>
    /// Opens one fresh, unpooled connection with <paramref name="options"/> and
    /// closes it again.
    /// </summary>
    /// <remarks>
    /// Unpooled on purpose: a pooled connection could answer from a session
    /// that authenticated with an earlier password, and this exists to prove
    /// that the password written down is the one the server accepts.
    /// </remarks>
    public static ProvisioningResult CanConnect(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            using var connection = new NpgsqlConnection(Unpooled(options, options.Username, options.Password, options.Database));
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT current_user;";
            var user = command.ExecuteScalar() as string;

            return ProvisioningResult.Ok($"Connected to {options.Host}:{options.Port} as '{user}'.");
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return ProvisioningResult.Failed(Explain(ex, options, options.Password));
        }
    }

    /// <summary>
    /// Changes the product role's own password, then points the pool at it.
    /// </summary>
    /// <remarks>
    /// <c>ALTER ROLE CURRENT_USER</c>: an ordinary role may change its own
    /// password, so rotation needs no administrator credential at all.
    /// </remarks>
    public static void RotatePassword(PostgresDatabase database, Secret newPassword)
    {
        ArgumentNullException.ThrowIfNull(database);

        var literal = PasswordLiteral(newPassword);

        database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"ALTER ROLE CURRENT_USER PASSWORD {literal};";
            command.ExecuteNonQuery();
        });

        database.UsePassword(newPassword);
    }

    /// <summary>
    /// A sentence about a failure that carries none of the secrets involved.
    /// </summary>
    /// <remarks>
    /// Fixed text for the cases an operator can act on, PostgreSQL's own
    /// message for the rest — never the exception's full text, whose shape is
    /// the driver's to change. And a last check: if any secret appears in the
    /// sentence anyway, the sentence is replaced wholesale.
    /// </remarks>
    public static string Explain(Exception exception, PostgresOptions target, params Secret[] secrets)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(secrets);

        var where = $"{target.Host}:{target.Port}";

        var detail = FindPostgresException(exception) is { } server
            ? server.SqlState switch
            {
                PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification =>
                    $"PostgreSQL at {where} rejected the credential ({server.SqlState}).",
                PostgresErrorCodes.InsufficientPrivilege =>
                    $"The account lacks a privilege this step needs: {server.MessageText} ({server.SqlState}). " +
                    "Setup needs an account with CREATEDB and CREATEROLE, or a superuser.",
                _ => $"PostgreSQL at {where} refused: {server.MessageText} ({server.SqlState}).",
            }
            : exception is NpgsqlException or TimeoutException
                ? $"PostgreSQL could not be reached at {where}."
                : $"The step failed ({exception.GetType().Name}).";

        foreach (var secret in secrets)
        {
            if (!secret.IsEmpty && detail.Contains(secret.Reveal(), StringComparison.Ordinal))
            {
                return $"The step failed at {where}; the server's message was withheld because it " +
                       "repeated a credential.";
            }
        }

        return detail;
    }

    internal static string Identifier(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a usable name: lower-case letters, digits and underscores, starting with a letter.",
                nameof(name));
        }

        return "\"" + name + "\"";
    }

    /// <summary>
    /// The <c>PASSWORD '...'</c> literal for a role: a SCRAM-SHA-256 verifier,
    /// never the password itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PostgreSQL's DDL takes no bind parameters, so whatever follows
    /// <c>PASSWORD</c> is text in the statement — and a statement is what a
    /// server with <c>log_statement = 'ddl'</c> writes to its log. Handing it a
    /// pre-computed verifier (the form <c>pg_authid</c> stores anyway) means the
    /// password never appears in a statement at all; the server stores the
    /// verifier as given and authenticates the password against it.
    /// </para>
    /// <para>
    /// Only generated passwords are accepted, and the verifier's alphabet
    /// (base64, <c>$</c>, <c>:</c>) has no quote; the doubling below is there so
    /// the literal stays correct even if that ever changes.
    /// </para>
    /// </remarks>
    internal static string PasswordLiteral(Secret password)
    {
        var value = password.Reveal();

        if (value.Length < 20 || !GeneratedPassword().IsMatch(value))
        {
            // Not the value: this message is about its shape, and a password
            // that is refused here was never going to reach the server.
            throw new ArgumentException(
                "Only a generated password (base64url, at least 20 characters) is written into DDL.",
                nameof(password));
        }

        return "'" + ScramVerifier(value, RandomNumberGenerator.GetBytes(16))
            .Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// A SCRAM-SHA-256 verifier in PostgreSQL's stored form (RFC 5802/7677):
    /// <c>SCRAM-SHA-256$iterations:salt$StoredKey:ServerKey</c>.
    /// </summary>
    internal static string ScramVerifier(string password, byte[] salt, int iterations = 4096)
    {
        // The generated alphabet is plain ASCII, so SASLprep leaves it as is.
        var salted = Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}");
    }

    private static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= 63 } &&
        ValidName().IsMatch(name) &&
        !name.StartsWith("pg_", StringComparison.Ordinal);

    private static void CheckName(List<string> problems, string what, string name)
    {
        if (!IsValidName(name))
        {
            problems.Add(
                $"The {what} name must be lower-case letters, digits and underscores, start with a " +
                "letter, not start with 'pg_', and be at most 63 characters.");
        }
    }

    private static void Undo(
        PostgresAdminCredential admin, PostgresOptions target, bool dropDatabase, bool dropRole, bool quiet = true)
    {
        try
        {
            using var server = OpenAsAdmin(admin, target, admin.MaintenanceDatabase);
            var role = Identifier(target.Username);
            var roleExists = Exists(server, "SELECT 1 FROM pg_roles WHERE rolname = @name", target.Username);

            if (dropDatabase &&
                Exists(server, "SELECT 1 FROM pg_database WHERE datname = @name", target.Database))
            {
                // The database belongs to the product's role, and an
                // administrator that is not a superuser may only drop it as
                // that owner — the same temporary SET the plan takes.
                if (roleExists)
                {
                    Execute(server, GrantSetToSelf(role, server.PostgreSqlVersion.Major));
                    Execute(server, $"SET ROLE {role};");
                }

                DropDatabaseWithForce(server, target.Database);

                if (roleExists)
                {
                    Execute(server, "RESET ROLE;");
                    Execute(server, $"REVOKE {role} FROM CURRENT_USER;");
                }
            }

            if (dropRole && roleExists)
            {
                Execute(server, $"DROP ROLE {role};");
            }
        }
        catch (Exception ex) when (quiet && ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // Best effort after a failure that is already being reported. The
            // operator is told what failed; a second failure while tidying up
            // would only replace that sentence with a less useful one.
        }
    }

    /// <summary>
    /// Runs <c>DROP DATABASE ... WITH (FORCE)</c>, tolerating the one race
    /// FORCE itself cannot help with.
    /// </summary>
    /// <remarks>
    /// FORCE terminates every backend connected to the database first — but a
    /// backend that is still authenticating has not been assigned a role yet,
    /// and PostgreSQL will not let even the role granted <c>SET</c> above
    /// terminate a process it cannot yet attribute to that role
    /// (<c>42501 permission denied to terminate process</c>). Measured live: a
    /// connection opened moments earlier by this same product's own schema
    /// check (<see cref="PostgresDatabase"/>'s pooled data source) can still be
    /// mid-handshake when this runs, especially under concurrent load. That
    /// handshake finishes in milliseconds either way, so retrying rather than
    /// failing outright is enough — no different from FORCE's own retry of the
    /// termination itself.
    /// </remarks>
    private static void DropDatabaseWithForce(NpgsqlConnection server, string database)
    {
        const int attempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Execute(server, $"DROP DATABASE {Identifier(database)} WITH (FORCE);");
                return;
            }
            catch (PostgresException ex) when (
                attempt < attempts &&
                // By code only: the message is localized by the server's
                // lc_messages, and a genuine 42501 still surfaces after the
                // last attempt, about 1.5 s later.
                ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    private static NpgsqlConnection OpenAsAdmin(PostgresAdminCredential admin, PostgresOptions target, string database)
    {
        var connection = new NpgsqlConnection(Unpooled(target, admin.Username, admin.Password, database));

        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // Unpooled, so that no pool keeps the administrator's connection string —
    // and with it the password — alive after the request that used it.
    private static string Unpooled(PostgresOptions target, string username, Secret password, string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = target.Host,
            Port = target.Port,
            Database = database,
            Username = username,
            Password = password.Reveal(),
            SslMode = target.RequireTls ? SslMode.Require : SslMode.Prefer,
            Pooling = false,
            Timeout = 15,
            // DROP DATABASE on Windows forces an immediate checkpoint before it
            // releases the files, so it takes as long as flushing everything the
            // server has dirtied. Measured on a live install writing ~26,000
            // rows a minute: 40.5 s for an empty database. 60 s left a failed
            // setup one busy minute away from timing out in its own rollback and
            // leaving the half-made database behind. These statements run once,
            // at setup; a long bound costs nothing when they are fast.
            CommandTimeout = 300,
        }.ConnectionString;

    private static bool Exists(NpgsqlConnection connection, string sql, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("name", name);
        return command.ExecuteScalar() is not null;
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static PostgresException? FindPostgresException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException server)
            {
                return server;
            }
        }

        return null;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedPassword();
}
