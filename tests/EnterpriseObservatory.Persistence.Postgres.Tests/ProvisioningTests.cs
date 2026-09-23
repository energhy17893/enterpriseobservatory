using EnterpriseObservatory.Application.Security;
using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// The create path against a real server, with a real administrator (G-DB).
/// </summary>
/// <remarks>
/// <para>
/// The administrator is a dedicated test role with CREATEDB and CREATEROLE and
/// <em>not</em> superuser — deliberately, because that is the case the
/// PostgreSQL 16+ SET rule bites, and a superuser would pass straight through
/// it. Supplied as <c>EO_TEST_PG_ADMIN_USER</c> / <c>EO_TEST_PG_ADMIN_PASSWORD</c>;
/// without them these skip and say why.
/// </para>
/// <para>
/// Every test works in a database and role of its own, named at random, and
/// drops both in its cleanup.
/// </para>
/// </remarks>
public sealed class ProvisioningTests : IDisposable
{
    private readonly string _name = "eo_prov_" + Guid.NewGuid().ToString("n")[..12];
    private readonly PostgresOptions _target;

    public ProvisioningTests()
    {
        var server = LiveDatabase.Options("public");

        _target = new PostgresOptions
        {
            Host = server.Host,
            Port = server.Port,
            Database = _name,
            Username = _name,
            Password = PostgresProvisioning.NewPassword(),
        };
    }

    private static string? SkipReason =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EO_TEST_PG_ADMIN_USER")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EO_TEST_PG_ADMIN_PASSWORD"))
            ? "No admin credential for the create-database path; set EO_TEST_PG_ADMIN_USER/PASSWORD."
            : null;

    private static PostgresAdminCredential Admin => new()
    {
        Username = Environment.GetEnvironmentVariable("EO_TEST_PG_ADMIN_USER") ?? string.Empty,
        Password = Secret.From(Environment.GetEnvironmentVariable("EO_TEST_PG_ADMIN_PASSWORD")),
    };

    public void Dispose()
    {
        if (SkipReason is null)
        {
            PostgresProvisioning.Drop(Admin, _target);
        }
    }

    private T AsAdmin<T>(Func<NpgsqlConnection, T> read)
    {
        using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = _target.Host,
            Port = _target.Port,
            Database = "postgres",
            Username = Admin.Username,
            Password = Admin.Password.Reveal(),
            Pooling = false,
            // Npgsql's default 30 s is shorter than a DROP DATABASE on a Windows
            // server under load (40.5 s measured), and a timed-out cleanup is how
            // this suite leaked its throwaway databases. Same bound as the
            // product's provisioning connections.
            CommandTimeout = 300,
        }.ConnectionString);

        connection.Open();
        return read(connection);
    }

    private static T Scalar<T>(NpgsqlConnection connection, string sql, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("name", name);
        return (T)command.ExecuteScalar()!;
    }

    [SkippableFact]
    public void Provisioning_creates_a_role_that_owns_its_database_and_is_no_kind_of_administrator()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var result = PostgresProvisioning.Provision(Admin, _target);

        Assert.True(result.Succeeded, result.Detail);
        Assert.True(PostgresProvisioning.CanConnect(_target).Succeeded);

        AsAdmin(connection =>
        {
            Assert.False(Scalar<bool>(connection, "SELECT rolsuper FROM pg_roles WHERE rolname = @name", _name));
            Assert.False(Scalar<bool>(connection, "SELECT rolcreatedb FROM pg_roles WHERE rolname = @name", _name));
            Assert.False(Scalar<bool>(connection, "SELECT rolcreaterole FROM pg_roles WHERE rolname = @name", _name));
            Assert.Equal(
                _name,
                Scalar<string>(
                    connection,
                    "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name",
                    _name));
            return 0;
        });

        // The schema went in through the product's own path.
        using var database = new PostgresDatabase(_target);
        var version = database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM schema_version;";
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

        // At least the version SchemaTests pins; the schema was applied, not merely begun.
        Assert.True(version >= 16, $"The schema stopped at version {version}.");
    }

    [SkippableFact]
    public void The_administrator_keeps_no_set_membership_of_the_product_role()
    {
        Skip.If(SkipReason is not null, SkipReason);

        Assert.True(PostgresProvisioning.Provision(Admin, _target).Succeeded);

        var lasting = AsAdmin(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT count(*) FROM pg_auth_members m " +
                "JOIN pg_roles r ON r.oid = m.roleid JOIN pg_roles u ON u.oid = m.member " +
                "WHERE r.rolname = @name AND u.rolname = current_user AND (m.set_option OR m.inherit_option);";
            command.Parameters.AddWithValue("name", _name);
            return (long)command.ExecuteScalar()!;
        });

        Assert.Equal(0, lasting);
    }

    [SkippableFact]
    public void Provisioning_refuses_names_that_already_exist_and_leaves_them_alone()
    {
        Skip.If(SkipReason is not null, SkipReason);

        Assert.True(PostgresProvisioning.Provision(Admin, _target).Succeeded);

        var again = PostgresProvisioning.Provision(
            Admin, _target with { Password = PostgresProvisioning.NewPassword() });

        Assert.False(again.Succeeded);
        Assert.Contains("already exists", again.Detail, StringComparison.Ordinal);

        // The first one was not undone by the second's refusal.
        Assert.True(PostgresProvisioning.CanConnect(_target).Succeeded);
    }

    [SkippableFact]
    public void A_rejected_administrator_credential_is_reported_without_repeating_it()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var wrong = Admin with { Password = Secret.From("definitely-not-the-password-" + _name) };

        var result = PostgresProvisioning.Provision(wrong, _target);

        Assert.False(result.Succeeded);
        Assert.Contains("rejected the credential", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong.Password.Reveal(), result.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void The_product_role_rotates_its_own_password_without_the_administrator()
    {
        Skip.If(SkipReason is not null, SkipReason);

        Assert.True(PostgresProvisioning.Provision(Admin, _target).Succeeded);

        var next = PostgresProvisioning.NewPassword();

        using (var database = new PostgresDatabase(_target))
        {
            PostgresProvisioning.RotatePassword(database, next);

            // The pool picked the new password up: a fresh physical connection
            // through it authenticates.
            NpgsqlConnection.ClearAllPools();
            Assert.Equal(1, database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                return (int)command.ExecuteScalar()!;
            }));
        }

        Assert.True(PostgresProvisioning.CanConnect(_target with { Password = next }).Succeeded);
        Assert.False(PostgresProvisioning.CanConnect(_target).Succeeded);
    }

    // --- an existing database a DBA made ---------------------------------

    private void AsAdminExecute(params string[] statements) => AsAdmin(connection =>
    {
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        return 0;
    });

    [SkippableFact]
    public void An_existing_role_that_owns_its_database_is_taken_over_and_the_schema_applied()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // What a DBA would have done, done here by the create path.
        Assert.True(PostgresProvisioning.Provision(Admin, _target).Succeeded);

        var adopted = PostgresProvisioning.Adopt(_target);

        Assert.True(adopted.Succeeded, adopted.Detail);
        Assert.DoesNotContain(_target.Password.Reveal(), adopted.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void An_existing_role_that_cannot_create_in_public_is_refused_with_a_sentence_for_the_dba()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // The DBA made the role and the database, but the database belongs to
        // the DBA: since PostgreSQL 15 that role cannot create a table in public.
        var password = PostgresProvisioning.NewPassword().Reveal();

        AsAdminExecute(
            $"CREATE ROLE \"{_name}\" LOGIN PASSWORD '{password}';",
            $"CREATE DATABASE \"{_name}\";",
            $"GRANT CONNECT ON DATABASE \"{_name}\" TO \"{_name}\";");

        try
        {
            var refused = PostgresProvisioning.Adopt(_target with { Password = Secret.From(password) });

            Assert.False(refused.Succeeded);
            Assert.Contains("cannot create tables in schema public", refused.Detail, StringComparison.Ordinal);
            Assert.Contains("GRANT CREATE ON SCHEMA \"public\"", refused.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(password, refused.Detail, StringComparison.Ordinal);
        }
        finally
        {
            // FORCE terminates whoever is still connected first, and the
            // Adopt() call above just closed its own probe connection — which
            // can still be finishing that close, server-side, when this runs.
            // The admin here owns the database directly (no SET ROLE trick:
            // there is nothing to SET ROLE to that would help drop someone
            // else's still-closing backend), so retrying past that instant is
            // simpler than racing it. Same fault PostgresProvisioning.Undo
            // retries around (42501, "terminate process"); measured live under
            // concurrent load, not merely theoretical.
            DropDatabaseIfExistsWithRetry(_name);
            AsAdminExecute($"DROP ROLE IF EXISTS \"{_name}\";");
        }
    }

    private void DropDatabaseIfExistsWithRetry(string name)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                AsAdminExecute($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE);");
                return;
            }
            catch (PostgresException ex) when (
                attempt < 5 &&
                ex.SqlState == PostgresErrorCodes.InsufficientPrivilege &&
                ex.MessageText.Contains("terminate process", StringComparison.Ordinal))
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    [SkippableFact]
    public void An_existing_role_with_createdb_is_refused()
    {
        Skip.If(SkipReason is not null, SkipReason);

        Assert.True(PostgresProvisioning.Provision(Admin, _target).Succeeded);

        // The standing rule: the product never runs with CREATEDB, CREATEROLE or SUPERUSER.
        AsAdminExecute($"ALTER ROLE \"{_name}\" CREATEDB;");

        var refused = PostgresProvisioning.Adopt(_target);

        Assert.False(refused.Succeeded);
        Assert.Contains("CREATEDB", refused.Detail, StringComparison.Ordinal);
        Assert.Contains("never runs", refused.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Drop_removes_both_the_database_and_the_role()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var provisioned = PostgresProvisioning.Provision(Admin, _target);
        Assert.True(provisioned.Succeeded, provisioned.Detail);

        var dropped = PostgresProvisioning.Drop(Admin, _target);
        Assert.True(dropped.Succeeded, dropped.Detail);

        AsAdmin(connection =>
        {
            Assert.Null(Scalar<object?>(connection, "SELECT 1 FROM pg_roles WHERE rolname = @name", _name));
            Assert.Null(Scalar<object?>(connection, "SELECT 1 FROM pg_database WHERE datname = @name", _name));
            return 0;
        });
    }
}
