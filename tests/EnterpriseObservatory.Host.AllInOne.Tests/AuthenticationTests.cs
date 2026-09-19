using System.Diagnostics;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Persistence.Sqlite;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Signing in, and the one-time problem of the first account.
/// </summary>
/// <remarks>
/// Against the real account store, because the things worth pinning down here —
/// that a lockout persists, that a name is not revealed by timing — are
/// properties of the whole path rather than of one function.
/// </remarks>
public class AuthenticationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);
    private const string Password = "correct horse battery staple";

    private readonly TestClock _clock = new(T0);

    private readonly ObservatoryDatabase _database = new(new SqliteStoreOptions
    {
        Path = string.Empty,
        InMemory = true,
    });

    private readonly SqliteUserAccountStore _accounts;
    private readonly AuthenticationService _authentication;

    public AuthenticationTests()
    {
        _accounts = new SqliteUserAccountStore(_database);
        _authentication = new AuthenticationService(_accounts, _clock);
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- the first account ------------------------------------------------

    [Fact]
    public void A_new_installation_has_nobody_and_says_so()
    {
        // The alternative is a login form that rejects every credential without
        // explaining why.
        Assert.True(_authentication.NeedsBootstrap);
    }

    [Fact]
    public void The_first_account_needs_the_token_this_run_issued()
    {
        // The alternative — a default administrator password — is how products
        // get taken over, and every scanner knows the common ones.
        var token = AuthenticationService.NewSetupToken();

        Assert.Equal(
            BootstrapFailure.BadToken,
            _authentication.CreateFirstAccount(token, "not-the-token", "ertugrul", Secret.From(Password)));

        Assert.True(_authentication.NeedsBootstrap);
    }

    [Fact]
    public void The_first_account_is_an_administrator()
    {
        var token = AuthenticationService.NewSetupToken();

        Assert.Equal(
            BootstrapFailure.None,
            _authentication.CreateFirstAccount(token, token, "Ertugrul", Secret.From(Password)));

        var account = _accounts.Find("ertugrul");

        Assert.NotNull(account);
        Assert.Equal(Role.Administrator, account.Role);
        Assert.False(_authentication.NeedsBootstrap);
    }

    [Fact]
    public void Once_there_is_an_account_the_setup_door_is_closed()
    {
        // However many tokens are floating about in old log files.
        var token = AuthenticationService.NewSetupToken();
        _authentication.CreateFirstAccount(token, token, "ertugrul", Secret.From(Password));

        Assert.Equal(
            BootstrapFailure.AlreadyConfigured,
            _authentication.CreateFirstAccount(token, token, "someone-else", Secret.From(Password)));
    }

    [Fact]
    public void A_short_password_is_refused()
    {
        var token = AuthenticationService.NewSetupToken();

        Assert.Equal(
            BootstrapFailure.Unusable,
            _authentication.CreateFirstAccount(token, token, "ertugrul", Secret.From("short")));
    }

    // --- signing in -------------------------------------------------------

    [Fact]
    public void The_right_password_signs_in()
    {
        Given("ertugrul", Role.Operator);

        var result = _authentication.SignIn("ertugrul", Secret.From(Password));

        Assert.True(result.Succeeded);
        Assert.Equal(Role.Operator, result.Account!.Role);
        Assert.Equal(T0, result.Account.LastSignedInUtc);
    }

    [Fact]
    public void The_username_is_not_case_sensitive()
    {
        // Two accounts for the same person, differing only in case, is a
        // support call and a permissions surprise.
        Given("ertugrul", Role.Viewer);

        Assert.True(_authentication.SignIn("  Ertugrul ", Secret.From(Password)).Succeeded);
    }

    [Fact]
    public void A_wrong_password_and_an_unknown_name_fail_the_same_way()
    {
        // Telling them apart turns the login form into a way of finding out who
        // has an account here.
        Given("ertugrul", Role.Viewer);

        var wrongPassword = _authentication.SignIn("ertugrul", Secret.From("wrong password here"));
        var unknownName = _authentication.SignIn("nobody", Secret.From(Password));

        Assert.Equal(SignInFailure.Rejected, wrongPassword.Failure);
        Assert.Equal(SignInFailure.Rejected, unknownName.Failure);
    }

    [Fact]
    public void An_unknown_name_costs_as_much_time_as_a_known_one()
    {
        // Otherwise the difference is measurable across a network and the login
        // form enumerates accounts. Deliberately a loose bound: this asserts
        // that the work is done at all, not a precise timing.
        Given("ertugrul", Role.Viewer);

        var known = Time(() => _authentication.SignIn("ertugrul", Secret.From("wrong password here")));
        var unknown = Time(() => _authentication.SignIn("nobody", Secret.From("wrong password here")));

        var ratio = unknown.TotalMilliseconds / Math.Max(known.TotalMilliseconds, 0.001);

        Assert.True(
            ratio is > 0.25 and < 4,
            $"Known name took {known.TotalMilliseconds:0}ms and unknown {unknown.TotalMilliseconds:0}ms.");
    }

    // --- lockout ----------------------------------------------------------

    [Fact]
    public void Enough_wrong_guesses_stop_the_account_for_a_while()
    {
        Given("ertugrul", Role.Viewer);

        for (var i = 0; i < LockoutPolicy.Default.MaxAttempts; i++)
        {
            _authentication.SignIn("ertugrul", Secret.From("wrong password here"));
        }

        var result = _authentication.SignIn("ertugrul", Secret.From(Password));

        Assert.Equal(SignInFailure.LockedOut, result.Failure);
        Assert.Equal(T0 + LockoutPolicy.Default.Duration, result.LockedUntilUtc);
    }

    [Fact]
    public void A_lockout_ends_on_its_own()
    {
        // Permanent lockout hands anyone who can reach the login page the
        // ability to lock out every administrator by guessing badly on purpose.
        Given("ertugrul", Role.Viewer);

        for (var i = 0; i < LockoutPolicy.Default.MaxAttempts; i++)
        {
            _authentication.SignIn("ertugrul", Secret.From("wrong password here"));
        }

        _clock.Advance(LockoutPolicy.Default.Duration + TimeSpan.FromSeconds(1));

        Assert.True(_authentication.SignIn("ertugrul", Secret.From(Password)).Succeeded);
    }

    [Fact]
    public void A_successful_sign_in_forgets_the_failures()
    {
        Given("ertugrul", Role.Viewer);

        _authentication.SignIn("ertugrul", Secret.From("wrong password here"));
        _authentication.SignIn("ertugrul", Secret.From(Password));

        Assert.Equal(0, _accounts.Find("ertugrul")!.FailedAttempts);
    }

    [Fact]
    public void A_lockout_survives_a_restart()
    {
        // Held only in memory, restarting the service would be the way past it.
        Given("ertugrul", Role.Viewer);

        for (var i = 0; i < LockoutPolicy.Default.MaxAttempts; i++)
        {
            _authentication.SignIn("ertugrul", Secret.From("wrong password here"));
        }

        var afterRestart = new AuthenticationService(new SqliteUserAccountStore(_database), _clock);

        Assert.Equal(
            SignInFailure.LockedOut,
            afterRestart.SignIn("ertugrul", Secret.From(Password)).Failure);
    }

    // --- what is stored ---------------------------------------------------

    [Fact]
    public void The_password_itself_is_never_stored()
    {
        Given("ertugrul", Role.Viewer);

        var stored = _accounts.Find("ertugrul")!.Password.Encoded;

        Assert.DoesNotContain(Password, stored, StringComparison.Ordinal);
        Assert.StartsWith("pbkdf2-sha256$", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_accounts_with_the_same_password_do_not_look_alike()
    {
        // A shared salt would let one cracked password reveal every account
        // that chose it, and make a rainbow table worth building.
        Given("first", Role.Viewer);
        Given("second", Role.Viewer);

        Assert.NotEqual(
            _accounts.Find("first")!.Password.Encoded,
            _accounts.Find("second")!.Password.Encoded);
    }

    [Fact]
    public void A_verifier_that_cannot_be_read_verifies_nothing()
    {
        // Failing closed is the only safe reading of a corrupted row.
        Assert.False(PasswordHash.Restore("not a hash").Verify(Secret.From(Password)));
        Assert.False(PasswordHash.Restore(string.Empty).Verify(Secret.From(Password)));
    }

    // --- roles ------------------------------------------------------------

    [Theory]
    [InlineData(Role.Viewer, Role.Viewer, true)]
    [InlineData(Role.Viewer, Role.Operator, false)]
    [InlineData(Role.Operator, Role.Viewer, true)]
    [InlineData(Role.Operator, Role.Operator, true)]
    [InlineData(Role.Operator, Role.Administrator, false)]
    [InlineData(Role.Administrator, Role.Operator, true)]
    public void A_role_includes_the_ones_below_it(Role held, Role required, bool allowed)
    {
        var account = new UserAccount
        {
            Username = "x",
            Password = PasswordHash.Create(Secret.From(Password)),
            Role = held,
            CreatedUtc = T0,
        };

        Assert.Equal(allowed, account.Can(required));
    }

    // --- fixtures ---------------------------------------------------------

    private void Given(string username, Role role) =>
        _accounts.TryAdd(new UserAccount
        {
            Username = UserAccount.Normalize(username),
            Password = PasswordHash.Create(Secret.From(Password)),
            Role = role,
            CreatedUtc = T0,
        });

    private static TimeSpan Time(Action work)
    {
        var stopwatch = Stopwatch.StartNew();
        work();
        return stopwatch.Elapsed;
    }
}
