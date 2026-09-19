namespace EnterpriseObservatory.Application.Security;

/// <summary>
/// What an account is allowed to do.
/// </summary>
/// <remarks>
/// <para>
/// Three, deliberately. Every role beyond the ones people actually need is one
/// more thing to get wrong when assigning them, and a product whose permission
/// model nobody understands ends up with everyone an administrator.
/// </para>
/// <para>
/// Ordered by what they include: an operator can do everything a viewer can,
/// and an administrator everything an operator can.
/// </para>
/// </remarks>
public enum Role
{
    /// <summary>Can see everything and change nothing.</summary>
    Viewer = 0,

    /// <summary>Can acknowledge, silence and clear alerts.</summary>
    Operator = 1,

    /// <summary>Can also manage accounts and configuration.</summary>
    Administrator = 2,
}

/// <summary>
/// Somebody who can sign in.
/// </summary>
/// <remarks>
/// Local accounts rather than domain ones, as the foundation. A monitoring
/// system that cannot be signed into while the directory is down is unavailable
/// at precisely the moment somebody needs to know what is broken — and "the
/// directory is down" is one of the things it exists to tell them. Directory
/// integration belongs on top of this, not instead of it. See ADR-0014.
/// </remarks>
public sealed record UserAccount
{
    public required string Username { get; init; }

    /// <summary>The stored verifier. Never the password.</summary>
    public required PasswordHash Password { get; init; }

    public required Role Role { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? LastSignedInUtc { get; init; }

    /// <summary>Consecutive failed attempts since the last success.</summary>
    public int FailedAttempts { get; init; }

    /// <summary>When the account stops refusing attempts, if it is refusing.</summary>
    public DateTimeOffset? LockedUntilUtc { get; init; }

    /// <summary>
    /// Whether the account is currently refusing attempts.
    /// </summary>
    /// <remarks>
    /// Time-limited rather than permanent. A permanent lockout hands anyone who
    /// can reach the login page the ability to lock out every administrator by
    /// guessing badly on purpose, which turns a brute-force defence into a
    /// denial of service.
    /// </remarks>
    public bool IsLockedAt(DateTimeOffset nowUtc) =>
        LockedUntilUtc is { } until && nowUtc < until;

    /// <summary>Whether this account may do what the role requires.</summary>
    public bool Can(Role required) => Role >= required;

    /// <summary>
    /// Normalizes a username so that case and stray spaces do not create a
    /// second account for the same person.
    /// </summary>
    public static string Normalize(string username) =>
        username?.Trim().ToLowerInvariant() ?? string.Empty;
}

/// <summary>How many failures, and for how long afterwards.</summary>
public sealed record LockoutPolicy
{
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// Long enough to make guessing hopeless, short enough that a real
    /// operator who fat-fingered their password twice is not locked out of the
    /// console during an incident.
    /// </summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(5);

    public static LockoutPolicy Default { get; } = new();
}
