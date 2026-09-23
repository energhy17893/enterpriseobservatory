using EnterpriseObservatory.Application.Security;
using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// The create path's statements, in order, with no server (G-DB).
/// </summary>
/// <remarks>
/// <para>
/// So the logic is covered on every machine, including those where the live
/// create test skips for want of an administrator credential.
/// </para>
/// <para>
/// The order is pinned exactly, because each step exists for a reason a
/// server enforces: the role must exist before a database can be owned by it;
/// from PostgreSQL 16 an administrator that is not a superuser holds only
/// ADMIN OPTION on a role it created and needs SET to name it as an owner; and
/// that SET is revoked straight after, so the administrator keeps no lasting
/// membership of the product's role.
/// </para>
/// </remarks>
public class ProvisioningPlanTests
{
    private static readonly Secret Generated = PostgresProvisioning.NewPassword();

    private static PostgresOptions Target(string role = "eo_product", string database = "eo_data") => new()
    {
        Host = "db.example.local",
        Port = 5432,
        Database = database,
        Username = role,
        Password = Generated,
    };

    [Fact]
    public void The_statements_are_create_role_then_grant_set_then_create_database_owned_by_it_then_revoke()
    {
        var plan = PostgresProvisioning.Plan(Target());

        Assert.Equal(4, plan.Statements.Count);

        Assert.Matches(
            "^CREATE ROLE \"eo_product\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS " +
            @"PASSWORD 'SCRAM-SHA-256\$4096:[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+';$",
            plan.Statements[0]);
        Assert.Equal("GRANT \"eo_product\" TO CURRENT_USER WITH SET TRUE;", plan.Statements[1]);
        Assert.Equal("CREATE DATABASE \"eo_data\" OWNER \"eo_product\";", plan.Statements[2]);
        Assert.Equal("REVOKE \"eo_product\" FROM CURRENT_USER;", plan.Statements[3]);
    }

    [Fact]
    public void Before_postgresql_16_the_grant_has_no_set_option_to_name()
    {
        var plan = PostgresProvisioning.Plan(Target(), serverMajorVersion: 15);

        Assert.Equal("GRANT \"eo_product\" TO CURRENT_USER;", plan.Statements[1]);
    }

    [Fact]
    public void The_password_itself_appears_in_no_statement_only_its_scram_verifier_does()
    {
        // PostgreSQL's DDL takes no parameters, and a server logging DDL would
        // write whatever follows PASSWORD. So what follows it is the verifier
        // pg_authid stores anyway, not the password.
        var plan = PostgresProvisioning.Plan(Target());

        Assert.All(plan.Statements, s => Assert.DoesNotContain(Generated.Reveal(), s, StringComparison.Ordinal));
        Assert.Contains("PASSWORD 'SCRAM-SHA-256$4096:", plan.Statements[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_verifier_is_salted_afresh_each_time()
    {
        Assert.NotEqual(PostgresProvisioning.Plan(Target()).Statements[0], PostgresProvisioning.Plan(Target()).Statements[0]);
    }

    [Theory]
    [InlineData("pa'ss'word-with-quotes-0123456789")]
    [InlineData("backslash\\-then-anything-0123456789")]
    [InlineData("short")]
    public void A_password_that_is_not_a_generated_one_is_never_written_into_ddl(string password)
    {
        var refused = Assert.Throws<ArgumentException>(() =>
            PostgresProvisioning.Plan(Target() with { Password = Secret.From(password) }));

        Assert.DoesNotContain(password, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eo\"; DROP DATABASE postgres; --")]
    [InlineData("Observatory")]
    [InlineData("pg_owned")]
    [InlineData("9starts_with_a_digit")]
    [InlineData("")]
    public void A_name_that_is_not_a_plain_identifier_is_refused_before_any_sql_is_built(string name)
    {
        Assert.Throws<ArgumentException>(() => PostgresProvisioning.Plan(Target(role: name)));
        Assert.Throws<ArgumentException>(() => PostgresProvisioning.Plan(Target(database: name)));
    }

    [Fact]
    public void The_product_role_cannot_be_postgres_or_the_administrator()
    {
        var admin = new PostgresAdminCredential { Username = "eo_admin", Password = Secret.From("x") };

        Assert.Contains(
            PostgresProvisioning.Problems(Target(role: "postgres"), admin),
            p => p.Contains("not 'postgres'", StringComparison.Ordinal));
        Assert.Contains(
            PostgresProvisioning.Problems(Target(role: "eo_admin"), admin),
            p => p.Contains("not 'postgres'", StringComparison.Ordinal));
        Assert.Empty(PostgresProvisioning.Problems(Target(), admin));
    }

    [Fact]
    public void Taking_over_an_existing_database_still_refuses_postgres_and_needs_a_password()
    {
        var problems = PostgresProvisioning.ProblemsForExisting(
            Target(role: "postgres") with { Password = Secret.Empty });

        Assert.Contains(problems, p => p.Contains("not 'postgres'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("password is required", StringComparison.Ordinal));
        Assert.Empty(PostgresProvisioning.ProblemsForExisting(Target()));
    }

    [Fact]
    public void A_generated_password_is_long_base64url_and_new_each_time()
    {
        var one = PostgresProvisioning.NewPassword().Reveal();
        var two = PostgresProvisioning.NewPassword().Reveal();

        Assert.Equal(43, one.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", one);
        Assert.NotEqual(one, two);
    }

    [Fact]
    public void A_server_message_that_repeats_a_secret_is_withheld()
    {
        var secret = Secret.From("do-not-echo-me-0123456789abcdef");
        var server = new PostgresException(
            $"syntax error at or near \"{secret.Reveal()}\"", "ERROR", "ERROR", "42601");

        var detail = PostgresProvisioning.Explain(server, Target(), secret);

        Assert.DoesNotContain(secret.Reveal(), detail, StringComparison.Ordinal);
        Assert.Contains("withheld", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_credential_is_reported_in_fixed_words()
    {
        var server = new PostgresException(
            "password authentication failed for user \"postgres\"", "FATAL", "FATAL", PostgresErrorCodes.InvalidPassword);

        var detail = PostgresProvisioning.Explain(server, Target());

        Assert.Equal("PostgreSQL at db.example.local:5432 rejected the credential (28P01).", detail);
    }
}
