using EnterpriseObservatory.Api;
using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Persistence.Sqlite;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

/// <summary>
/// Adding, changing and removing accounts.
/// </summary>
/// <remarks>
/// The one that matters most is the refusal: an installation with no
/// administrator has to be repaired by editing the database, and whoever does
/// that will be doing it during an incident.
/// </remarks>
public class AccountServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);
    private const string Password = "correct horse battery staple";
    private const string Replacement = "a different long phrase";

    private readonly TestClock _clock = new(T0);

    private readonly ObservatoryDatabase _database = new(new SqliteStoreOptions
    {
        Path = string.Empty,
        InMemory = true,
    });

    private readonly SqliteUserAccountStore _accounts;
    private readonly AccountService _service;

    public AccountServiceTests()
    {
        _accounts = new SqliteUserAccountStore(_database);
        _service = new AccountService(_accounts, _clock);
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- creating ---------------------------------------------------------

    [Fact]
    public void An_account_can_be_created_and_signed_in_with()
    {
        var result = _service.Create("Ertugrul", Secret.From(Password), Role.Operator);

        Assert.True(result.Applied);
        Assert.Equal("ertugrul", result.Account!.Username);

        var authentication = new AuthenticationService(_accounts, _clock);

        Assert.True(authentication.SignIn("ertugrul", Secret.From(Password)).Succeeded);
    }

    [Fact]
    public void A_name_already_taken_is_refused()
    {
        _service.Create("ertugrul", Secret.From(Password), Role.Operator);

        var second = _service.Create("ERTUGRUL", Secret.From(Password), Role.Viewer);

        Assert.Equal(AccountChangeFailure.NameTaken, second.Failure);
    }

    [Fact]
    public void A_short_password_is_refused()
    {
        Assert.Equal(
            AccountChangeFailure.Unusable,
            _service.Create("ertugrul", Secret.From("short"), Role.Viewer).Failure);
    }

    // --- the refusal that matters -----------------------------------------

    [Fact]
    public void The_last_administrator_cannot_be_removed()
    {
        _service.Create("admin", Secret.From(Password), Role.Administrator);
        _service.Create("operator", Secret.From(Password), Role.Operator);

        var result = _service.Remove("admin");

        Assert.Equal(AccountChangeFailure.WouldLockEverybodyOut, result.Failure);
        Assert.NotNull(_accounts.Find("admin"));
    }

    [Fact]
    public void The_last_administrator_cannot_be_demoted_either()
    {
        // Same outcome as removal by a different route, so the same refusal.
        _service.Create("admin", Secret.From(Password), Role.Administrator);

        Assert.Equal(
            AccountChangeFailure.WouldLockEverybodyOut,
            _service.ChangeRole("admin", Role.Operator).Failure);
    }

    [Fact]
    public void An_administrator_can_go_once_there_is_another()
    {
        _service.Create("first", Secret.From(Password), Role.Administrator);
        _service.Create("second", Secret.From(Password), Role.Administrator);

        Assert.True(_service.Remove("first").Applied);
        Assert.Single(_accounts.All());
    }

    [Fact]
    public void Promoting_somebody_first_unblocks_the_handover()
    {
        // The sequence an administrator actually follows when they leave.
        _service.Create("leaving", Secret.From(Password), Role.Administrator);
        _service.Create("arriving", Secret.From(Password), Role.Operator);

        Assert.Equal(
            AccountChangeFailure.WouldLockEverybodyOut,
            _service.Remove("leaving").Failure);

        Assert.True(_service.ChangeRole("arriving", Role.Administrator).Applied);
        Assert.True(_service.Remove("leaving").Applied);
    }

    // --- changing a password ----------------------------------------------

    [Fact]
    public void Changing_your_own_password_needs_the_current_one()
    {
        // The session already proves identity, but a session left open on an
        // unlocked workstation is the common case and must not be enough to
        // take the account over.
        _service.Create("ertugrul", Secret.From(Password), Role.Operator);

        Assert.Equal(
            AccountChangeFailure.WrongPassword,
            _service.ChangeOwnPassword("ertugrul", Secret.From("wrong"), Secret.From(Replacement))
                .Failure);
    }

    [Fact]
    public void The_new_password_is_the_one_that_works_afterwards()
    {
        _service.Create("ertugrul", Secret.From(Password), Role.Operator);
        _service.ChangeOwnPassword("ertugrul", Secret.From(Password), Secret.From(Replacement));

        var authentication = new AuthenticationService(_accounts, _clock);

        Assert.False(authentication.SignIn("ertugrul", Secret.From(Password)).Succeeded);
        Assert.True(authentication.SignIn("ertugrul", Secret.From(Replacement)).Succeeded);
    }

    [Fact]
    public void An_administrator_can_reset_a_forgotten_password()
    {
        // The answer to the one administrator who forgot theirs was previously
        // "edit the database".
        _service.Create("ertugrul", Secret.From(Password), Role.Operator);

        Assert.True(_service.ResetPassword("ertugrul", Secret.From(Replacement)).Applied);

        var authentication = new AuthenticationService(_accounts, _clock);

        Assert.True(authentication.SignIn("ertugrul", Secret.From(Replacement)).Succeeded);
    }

    [Fact]
    public void Changing_a_password_clears_a_lockout()
    {
        // Somebody who can prove the old password is not the attacker the
        // lockout was for.
        _service.Create("ertugrul", Secret.From(Password), Role.Operator);

        var authentication = new AuthenticationService(_accounts, _clock);

        for (var i = 0; i < LockoutPolicy.Default.MaxAttempts; i++)
        {
            authentication.SignIn("ertugrul", Secret.From("wrong password here"));
        }

        Assert.True(_accounts.Find("ertugrul")!.IsLockedAt(T0));

        _service.ChangeOwnPassword("ertugrul", Secret.From(Password), Secret.From(Replacement));

        Assert.False(_accounts.Find("ertugrul")!.IsLockedAt(T0));
    }

    [Fact]
    public void A_reset_clears_a_lockout_too()
    {
        // Otherwise an administrator resets the password and the person still
        // cannot get in, for a reason nothing on the screen explains.
        _service.Create("ertugrul", Secret.From(Password), Role.Operator);

        var authentication = new AuthenticationService(_accounts, _clock);

        for (var i = 0; i < LockoutPolicy.Default.MaxAttempts; i++)
        {
            authentication.SignIn("ertugrul", Secret.From("wrong password here"));
        }

        _service.ResetPassword("ertugrul", Secret.From(Replacement));

        Assert.True(authentication.SignIn("ertugrul", Secret.From(Replacement)).Succeeded);
    }

    // --- durability -------------------------------------------------------

    [Fact]
    public void Accounts_survive_a_restart()
    {
        _service.Create("ertugrul", Secret.From(Password), Role.Operator);

        var afterRestart = new SqliteUserAccountStore(_database);

        Assert.Equal(Role.Operator, afterRestart.Find("ertugrul")!.Role);
    }

    // --- a session against a changed account ------------------------------

    [Fact]
    public void A_removed_accounts_session_stops_working()
    {
        // Without this, removing an account removes nothing: the cookie keeps
        // working for hours. Verified live, and pinned here because it is the
        // rule that decides whether removal means anything.
        _service.Create("ertugrul", Secret.From(Password), Role.Operator);

        var principal = AuthenticationApi.PrincipalFor(_accounts.Find("ertugrul")!);

        Assert.Equal(
            AuthenticationApi.PrincipalStatus.Valid,
            AuthenticationApi.Revalidate(_accounts, principal).Status);

        _service.Remove("ertugrul");

        Assert.Equal(
            AuthenticationApi.PrincipalStatus.Gone,
            AuthenticationApi.Revalidate(_accounts, principal).Status);
    }

    [Fact]
    public void A_role_change_reaches_a_session_already_open()
    {
        // Both directions matter. A demotion that waits for the next sign-in
        // leaves somebody holding a power they were just told they do not have;
        // a promotion that waits tells somebody they lack a role they were just
        // given.
        _service.Create("ertugrul", Secret.From(Password), Role.Viewer);

        var principal = AuthenticationApi.PrincipalFor(_accounts.Find("ertugrul")!);

        _service.ChangeRole("ertugrul", Role.Operator);

        var (status, replacement) = AuthenticationApi.Revalidate(_accounts, principal);

        Assert.Equal(AuthenticationApi.PrincipalStatus.Stale, status);
        Assert.Equal(Role.Operator, AuthenticationApi.RoleOf(replacement!));
    }

    [Fact]
    public void A_principal_with_no_name_is_not_somebody()
    {
        Assert.Equal(
            AuthenticationApi.PrincipalStatus.Gone,
            AuthenticationApi.Revalidate(_accounts, null).Status);
    }

    [Fact]
    public void Acting_on_an_account_that_is_not_there_is_reported()
    {
        Assert.Equal(AccountChangeFailure.NotFound, _service.Remove("nobody").Failure);
        Assert.Equal(AccountChangeFailure.NotFound, _service.ChangeRole("nobody", Role.Viewer).Failure);
        Assert.Equal(
            AccountChangeFailure.NotFound,
            _service.ResetPassword("nobody", Secret.From(Replacement)).Failure);
    }
}
