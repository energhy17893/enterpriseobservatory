using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EnterpriseObservatory.Application.Security;

/// <summary>
/// A stored password verifier.
/// </summary>
/// <remarks>
/// <para>
/// PBKDF2-HMAC-SHA256, which is in the base class library. Argon2id resists
/// GPU cracking better and would be the choice if a dependency were free, but
/// ADR-0001 keeps this product to one MSI with nothing to install — and a
/// correctly parameterised PBKDF2 is what ASP.NET Core Identity itself ships.
/// The parameters are recorded in the stored value, so raising them later
/// upgrades accounts as people sign in rather than invalidating them.
/// </para>
/// <para>
/// Format: <c>pbkdf2-sha256$iterations$salt$hash</c>, both parts base64. Self
/// describing on purpose: somebody looking at the database years from now can
/// tell what it is without finding this file.
/// </para>
/// </remarks>
public readonly record struct PasswordHash
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>
    /// The work factor.
    /// </summary>
    /// <remarks>
    /// OWASP's 2023 figure for PBKDF2-HMAC-SHA256. It costs a fraction of a
    /// second per sign-in, which nobody notices, and multiplies the cost of
    /// guessing by the same factor.
    /// </remarks>
    public const int DefaultIterations = 600_000;

    private readonly string? _encoded;

    private PasswordHash(string encoded) => _encoded = encoded;

    /// <summary>The stored form, safe to write to a database.</summary>
    public string Encoded => _encoded ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(_encoded);

    public override string ToString() => Encoded;

    /// <summary>Hashes a new password.</summary>
    public static PasswordHash Create(Secret password, int iterations = DefaultIterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, iterations);

        return new PasswordHash(string.Join(
            '$',
            Algorithm,
            iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash)));
    }

    /// <summary>Reads a verifier back out of storage.</summary>
    public static PasswordHash Restore(string encoded) => new(encoded);

    /// <summary>
    /// Whether a password matches.
    /// </summary>
    /// <remarks>
    /// Compared in constant time. An ordinary comparison returns as soon as two
    /// bytes differ, and how long that takes is measurable across a network.
    /// </remarks>
    public bool Verify(Secret password)
    {
        if (IsEmpty)
        {
            return false;
        }

        var parts = Encoded.Split('$');

        if (parts.Length != 4 ||
            !string.Equals(parts[0], Algorithm, StringComparison.Ordinal) ||
            !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            // A verifier that cannot be parsed verifies nothing. Failing closed
            // is the only safe reading of a corrupted row.
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Derive(password, salt, iterations), expected);
    }

    /// <summary>
    /// A verifier that matches nothing, for when the account does not exist.
    /// </summary>
    /// <remarks>
    /// Verified against anyway, so that signing in with an unknown username
    /// takes as long as signing in with a known one. Otherwise the login form
    /// becomes a way to discover who has an account here.
    /// </remarks>
    public static PasswordHash Decoy { get; } = Create(Secret.From("decoy"), iterations: DefaultIterations);

    private static byte[] Derive(Secret password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password.Reveal()), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
