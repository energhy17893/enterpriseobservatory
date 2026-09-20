using System.Diagnostics;
using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Signing in, and the one-time problem of the first account.
/// </summary>
/// <remarks>
/// Against the real account store, because the things worth pinning down here —
/// that a lockout persists, that a name is not revealed by timing — are
/// properties of the whole path rather than of one function.
/// </remarks>
public class AuthenticationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);
    private const string Password = "correct horse battery staple";

    private readonly TestClock _clock = new(T0);

    private readonly InMemoryUserAccountStore _accounts;
    private readonly AuthenticationService _authentication;

    public AuthenticationTests()
    {
        _accounts = new InMemoryUserAccountStore();
        _authentication = new AuthenticationService(_accounts, _clock);
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
    public void Five_simultaneous_wrong_passwords_lock_the_account_just_as_five_sequential_ones_do()
    {
        // The traffic the lockout exists to stop is the traffic that arrives
        // all at once, and that is precisely the shape it used to miss. Read
        // the counter, decide from the copy, write the answer back: five
        // attempts posted together all read nought and all store one, so the
        // account never locks, the burst repeats for as long as the attacker
        // likes, and the product goes on telling its operators that it locks
        // accounts out after five.
        Given("ertugrul", Role.Viewer);

        var attempts = LockoutPolicy.Default.MaxAttempts;

        // Real threads and a barrier rather than the thread pool: the whole
        // claim is that the attempts overlap, and a pool free to run them one
        // after another would prove nothing while looking like it had.
        using var together = new Barrier(attempts);
        var guessers = new Thread[attempts];

        for (var i = 0; i < attempts; i++)
        {
            guessers[i] = new Thread(() =>
            {
                together.SignalAndWait();
                _authentication.SignIn("ertugrul", Secret.From("wrong password here"));
            });

            guessers[i].Start();
        }

        foreach (var guesser in guessers)
        {
            guesser.Join();
        }

        Assert.Equal(attempts, _accounts.Find("ertugrul")!.FailedAttempts);

        var next = _authentication.SignIn("ertugrul", Secret.From(Password));

        Assert.Equal(SignInFailure.LockedOut, next.Failure);
        Assert.Equal(T0 + LockoutPolicy.Default.Duration, next.LockedUntilUtc);
    }

    [Fact]
    public void A_sign_in_does_not_restore_a_password_an_administrator_has_just_replaced()
    {
        // A success writes the whole account, and built from the copy it read
        // it puts every field back as it stood then — the verifier included.
        // An administrator resetting a password because it has leaked, while
        // that account happens to be signing in, would find the leaked one
        // working again afterwards and nothing anywhere saying why.
        const string Replacement = "an entirely different passphrase";

        Given("ertugrul", Role.Viewer);

        var interfering = new InterferingAccountStore(_accounts);
        var authentication = new AuthenticationService(interfering, _clock);
        var administration = new AccountService(_accounts, _clock);

        interfering.AfterTheNextRead(
            () => administration.ResetPassword("ertugrul", Secret.From(Replacement)));

        authentication.SignIn("ertugrul", Secret.From(Password));

        var stored = _accounts.Find("ertugrul")!.Password;

        Assert.True(stored.Verify(Secret.From(Replacement)));
        Assert.False(stored.Verify(Secret.From(Password)));
    }

    [Fact]
    public void Changing_a_role_does_not_end_a_lockout_that_began_while_the_change_was_in_flight()
    {
        // The other half of the same defect, and the quieter half. A role
        // change writes the whole account too, so a lockout that started after
        // it read the row is written back as "not locked" — an administrator
        // making an unrelated change hands the attacker the account back, and
        // the audit trail shows a role change.
        Given("ertugrul", Role.Viewer);

        var interfering = new InterferingAccountStore(_accounts);
        var administration = new AccountService(interfering, _clock);

        interfering.AfterTheNextRead(() =>
        {
            for (var i = 0; i < LockoutPolicy.Default.MaxAttempts; i++)
            {
                _authentication.SignIn("ertugrul", Secret.From("wrong password here"));
            }
        });

        administration.ChangeRole("ertugrul", Role.Operator);

        var account = _accounts.Find("ertugrul")!;

        Assert.Equal(Role.Operator, account.Role);
        Assert.True(account.IsLockedAt(T0));
        Assert.Equal(LockoutPolicy.Default.MaxAttempts, account.FailedAttempts);
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

    /// <summary>
    /// A store that lets a test land somebody else's change in the gap between
    /// a caller's read and its write.
    /// </summary>
    /// <remarks>
    /// That gap is where the whole defect lived, and it is not reachable by
    /// running two threads and hoping. Scheduled rather than raced, so a test
    /// about it either passes or fails rather than doing so one time in thirty.
    /// </remarks>
    private sealed class InterferingAccountStore(IUserAccountStore inner) : IUserAccountStore
    {
        private Action? _pending;

        public void AfterTheNextRead(Action interfere) => _pending = interfere;

        public UserAccount? Find(string username)
        {
            var found = inner.Find(username);

            // Taken before it runs, so that interference which itself reads
            // through this store does not set itself off again.
            var interfere = _pending;
            _pending = null;
            interfere?.Invoke();

            return found;
        }

        public bool Any => inner.Any;

        public IReadOnlyList<UserAccount> All() => inner.All();

        public bool TryAdd(UserAccount account) => inner.TryAdd(account);

        public UserAccount? Mutate(string username, Func<UserAccount, UserAccount> change) =>
            inner.Mutate(username, change);

        public bool Remove(string username) => inner.Remove(username);
    }

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
