using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Application.Security;

/// <summary>Why an account change was refused.</summary>
public enum AccountChangeFailure
{
    None = 0,

    /// <summary>No account by that name.</summary>
    NotFound,

    /// <summary>That username is already taken.</summary>
    NameTaken,

    /// <summary>The username or password does not meet the minimum.</summary>
    Unusable,

    /// <summary>The current password given does not match.</summary>
    WrongPassword,

    /// <summary>
    /// It would leave the installation with no administrator.
    /// </summary>
    /// <remarks>
    /// The one change the product refuses outright. An installation nobody can
    /// administer is one that has to be repaired by editing the database, and
    /// the person doing it will be doing it during an incident.
    /// </remarks>
    WouldLockEverybodyOut,
}

/// <summary>What an account change did.</summary>
public sealed record AccountChangeResult
{
    public required bool Applied { get; init; }

    public UserAccount? Account { get; init; }

    public AccountChangeFailure Failure { get; init; }

    public static AccountChangeResult Done(UserAccount account) =>
        new() { Applied = true, Account = account };

    public static AccountChangeResult Refused(AccountChangeFailure failure) =>
        new() { Applied = false, Failure = failure };
}

/// <summary>
/// Adding, changing and removing accounts.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="AuthenticationService"/>, which answers "is this
/// you". This answers "who exists", and the two have different callers: one is
/// reached by anybody with the login form, the other only by an administrator.
/// </para>
/// <para>
/// See ADR-0014.
/// </para>
/// </remarks>
public sealed class AccountService(IUserAccountStore accounts, IClock clock)
{
    private readonly IUserAccountStore _accounts =
        accounts ?? throw new ArgumentNullException(nameof(accounts));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public IReadOnlyList<UserAccount> All() => _accounts.All();

    /// <summary>Creates an account.</summary>
    public AccountChangeResult Create(string username, Secret password, Role role)
    {
        var normalized = UserAccount.Normalize(username);

        if (normalized.Length == 0 || !IsUsablePassword(password))
        {
            return AccountChangeResult.Refused(AccountChangeFailure.Unusable);
        }

        var account = new UserAccount
        {
            Username = normalized,
            Password = PasswordHash.Create(password),
            Role = role,
            CreatedUtc = _clock.UtcNow,
        };

        return _accounts.TryAdd(account)
            ? AccountChangeResult.Done(account)
            : AccountChangeResult.Refused(AccountChangeFailure.NameTaken);
    }

    /// <summary>
    /// Changes one's own password, having proved the current one.
    /// </summary>
    /// <remarks>
    /// The current password is required even though the session already proves
    /// identity. A session left open on an unlocked workstation is the common
    /// case, and it should not be enough to take the account over.
    /// </remarks>
    public AccountChangeResult ChangeOwnPassword(string username, Secret current, Secret replacement)
    {
        if (_accounts.Find(UserAccount.Normalize(username)) is not { } account)
        {
            return AccountChangeResult.Refused(AccountChangeFailure.NotFound);
        }

        if (!account.Password.Verify(current))
        {
            return AccountChangeResult.Refused(AccountChangeFailure.WrongPassword);
        }

        if (!IsUsablePassword(replacement))
        {
            return AccountChangeResult.Refused(AccountChangeFailure.Unusable);
        }

        return Store(account with
        {
            Password = PasswordHash.Create(replacement),
            // A change of password clears a lockout: somebody who can prove the
            // old one is not the attacker the lockout was for.
            FailedAttempts = 0,
            LockedUntilUtc = null,
        });
    }

    /// <summary>
    /// Sets another account's password, without knowing the old one.
    /// </summary>
    /// <remarks>
    /// The answer to a forgotten password, and an administrator-only power for
    /// the obvious reason. It does not end that account's open sessions —
    /// session revocation is not built, and ADR-0014 records that.
    /// </remarks>
    public AccountChangeResult ResetPassword(string username, Secret replacement)
    {
        if (_accounts.Find(UserAccount.Normalize(username)) is not { } account)
        {
            return AccountChangeResult.Refused(AccountChangeFailure.NotFound);
        }

        if (!IsUsablePassword(replacement))
        {
            return AccountChangeResult.Refused(AccountChangeFailure.Unusable);
        }

        return Store(account with
        {
            Password = PasswordHash.Create(replacement),
            FailedAttempts = 0,
            LockedUntilUtc = null,
        });
    }

    /// <summary>Changes what an account is allowed to do.</summary>
    public AccountChangeResult ChangeRole(string username, Role role)
    {
        if (_accounts.Find(UserAccount.Normalize(username)) is not { } account)
        {
            return AccountChangeResult.Refused(AccountChangeFailure.NotFound);
        }

        if (WouldRemoveTheLastAdministrator(account, becoming: role))
        {
            return AccountChangeResult.Refused(AccountChangeFailure.WouldLockEverybodyOut);
        }

        return Store(account with { Role = role });
    }

    /// <summary>Removes an account.</summary>
    public AccountChangeResult Remove(string username)
    {
        if (_accounts.Find(UserAccount.Normalize(username)) is not { } account)
        {
            return AccountChangeResult.Refused(AccountChangeFailure.NotFound);
        }

        if (WouldRemoveTheLastAdministrator(account, becoming: null))
        {
            return AccountChangeResult.Refused(AccountChangeFailure.WouldLockEverybodyOut);
        }

        _accounts.Remove(account.Username);

        return AccountChangeResult.Done(account);
    }

    /// <summary>
    /// Whether changing or removing this account would leave nobody able to
    /// administer the installation.
    /// </summary>
    /// <remarks>
    /// Checked for demotion and removal alike, because they have the same
    /// outcome. The alternative to refusing is an installation that can only be
    /// repaired by editing the database by hand — during an incident, by
    /// somebody who has just locked themselves out.
    /// </remarks>
    private bool WouldRemoveTheLastAdministrator(UserAccount account, Role? becoming)
    {
        if (account.Role != Role.Administrator || becoming == Role.Administrator)
        {
            return false;
        }

        return _accounts.All().Count(a => a.Role == Role.Administrator) <= 1;
    }

    private AccountChangeResult Store(UserAccount account)
    {
        _accounts.Update(account);

        return AccountChangeResult.Done(account);
    }

    private static bool IsUsablePassword(Secret password) =>
        password.Reveal().Length >= AuthenticationService.MinimumPasswordLength;
}
