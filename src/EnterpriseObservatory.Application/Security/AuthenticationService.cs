using System.Security.Cryptography;
using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Application.Security;

/// <summary>Where accounts live.</summary>
/// <remarks>
/// In the state database rather than a file, so that an account survives a
/// restart for the same reason an acknowledgement does. See ADR-0011.
/// </remarks>
public interface IUserAccountStore
{
    /// <summary>Whether any account exists at all.</summary>
    bool Any { get; }

    UserAccount? Find(string username);

    IReadOnlyList<UserAccount> All();

    /// <summary>Creates an account, or returns false if the name is taken.</summary>
    bool TryAdd(UserAccount account);

    void Update(UserAccount account);

    /// <summary>Removes an account, or returns false if it was not there.</summary>
    bool Remove(string username);
}

/// <summary>Why a sign-in did not succeed.</summary>
public enum SignInFailure
{
    None = 0,

    /// <summary>Wrong username or wrong password. Deliberately one outcome.</summary>
    Rejected,

    /// <summary>Too many recent failures; the account is refusing for now.</summary>
    LockedOut,
}

/// <summary>What a sign-in attempt produced.</summary>
public sealed record SignInResult
{
    public required bool Succeeded { get; init; }

    public UserAccount? Account { get; init; }

    public SignInFailure Failure { get; init; }

    /// <summary>When the lockout ends, when that is why this failed.</summary>
    public DateTimeOffset? LockedUntilUtc { get; init; }
}

/// <summary>Why the first account could not be created.</summary>
public enum BootstrapFailure
{
    None = 0,

    /// <summary>An account already exists; this door is closed for good.</summary>
    AlreadyConfigured,

    /// <summary>The setup token did not match, or has expired.</summary>
    BadToken,

    /// <summary>The username or password does not meet the minimum.</summary>
    Unusable,
}

/// <summary>
/// Signing in, and the one-time problem of the first account.
/// </summary>
/// <remarks>
/// The single place a credential is checked, as <see cref="Alerts.AlertOperations"/>
/// is the single place operator intent reaches an alert.
/// </remarks>
public sealed class AuthenticationService(
    IUserAccountStore accounts,
    IClock clock,
    LockoutPolicy? lockout = null)
{
    private readonly IUserAccountStore _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly LockoutPolicy _lockout = lockout ?? LockoutPolicy.Default;

    /// <summary>The shortest password the product will store.</summary>
    /// <remarks>
    /// A length floor and nothing else. Composition rules — a digit, a symbol,
    /// a capital — measurably push people towards <c>Password1!</c> and are
    /// advised against by both NIST and the NCSC; length is what actually
    /// costs an attacker anything.
    /// </remarks>
    public const int MinimumPasswordLength = 12;

    /// <summary>Whether the product still needs its first account.</summary>
    public bool NeedsBootstrap => !_accounts.Any;

    // --- signing in -------------------------------------------------------

    public SignInResult SignIn(string username, Secret password)
    {
        var now = _clock.UtcNow;
        var account = _accounts.Find(UserAccount.Normalize(username));

        if (account is null)
        {
            // Verified against a decoy anyway, so that an unknown username
            // takes as long as a known one. Otherwise the login form becomes a
            // way to find out who has an account here.
            _ = PasswordHash.Decoy.Verify(password);

            return new SignInResult { Succeeded = false, Failure = SignInFailure.Rejected };
        }

        if (account.IsLockedAt(now))
        {
            return new SignInResult
            {
                Succeeded = false,
                Failure = SignInFailure.LockedOut,
                LockedUntilUtc = account.LockedUntilUtc,
            };
        }

        if (!account.Password.Verify(password))
        {
            _accounts.Update(RecordFailure(account, now));

            return new SignInResult { Succeeded = false, Failure = SignInFailure.Rejected };
        }

        var signedIn = account with
        {
            LastSignedInUtc = now,
            FailedAttempts = 0,
            LockedUntilUtc = null,
        };

        _accounts.Update(signedIn);

        return new SignInResult { Succeeded = true, Account = signedIn };
    }

    private UserAccount RecordFailure(UserAccount account, DateTimeOffset now)
    {
        var attempts = account.FailedAttempts + 1;

        return account with
        {
            FailedAttempts = attempts,
            LockedUntilUtc = attempts >= _lockout.MaxAttempts ? now + _lockout.Duration : null,
        };
    }

    // --- the first account ------------------------------------------------

    /// <summary>
    /// Mints the one-time token that lets somebody create the first account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alternative — shipping a default administrator password — is how
    /// products get taken over, and every scanner on the internet knows the
    /// common ones. The alternative to that, leaving the first run wide open,
    /// means whoever reaches it first owns the installation.
    /// </para>
    /// <para>
    /// So the host generates a token at startup and writes it where only
    /// somebody who can already read the service's own output can see it. It
    /// is not stored: nothing to steal from the database, and restarting the
    /// service issues a new one.
    /// </para>
    /// </remarks>
    public static string NewSetupToken() =>
        // URL and copy-paste safe: this gets read off a console and typed into
        // a form, so it must survive being selected by double-click.
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>
    /// Creates the first account, if the token matches and there is none yet.
    /// </summary>
    /// <param name="expectedToken">The token this run issued.</param>
    /// <param name="offeredToken">What the caller presented.</param>
    public BootstrapFailure CreateFirstAccount(
        string expectedToken, string offeredToken, string username, Secret password)
    {
        if (_accounts.Any)
        {
            // Checked first and re-checked at insert. Once there is an
            // administrator this door is closed for good, however many tokens
            // are floating about in old log files.
            return BootstrapFailure.AlreadyConfigured;
        }

        if (!FixedTimeEquals(expectedToken, offeredToken))
        {
            return BootstrapFailure.BadToken;
        }

        var normalized = UserAccount.Normalize(username);

        if (normalized.Length == 0 || password.Reveal().Length < MinimumPasswordLength)
        {
            return BootstrapFailure.Unusable;
        }

        var created = _accounts.TryAdd(new UserAccount
        {
            Username = normalized,
            Password = PasswordHash.Create(password),
            Role = Role.Administrator,
            CreatedUtc = _clock.UtcNow,
        });

        return created ? BootstrapFailure.None : BootstrapFailure.AlreadyConfigured;
    }

    /// <summary>Compares two tokens without leaking how much of one matched.</summary>
    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left ?? string.Empty),
            System.Text.Encoding.UTF8.GetBytes(right ?? string.Empty));
}
