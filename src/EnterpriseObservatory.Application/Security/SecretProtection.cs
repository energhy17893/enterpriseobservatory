namespace EnterpriseObservatory.Application.Security;

/// <summary>
/// Turns a secret into something that can be stored, and back.
/// </summary>
/// <remarks>
/// <para>
/// Reversible, deliberately, and this is the uncomfortable part of the design
/// so it is written down rather than implied. A password used to authenticate
/// against vCenter has to be presented to vCenter, so the product must be able
/// to recover the original value unattended, at three in the morning, with
/// nobody to type anything. A hash cannot do that. Every scheme that can is
/// reversible by definition, and the only real question is what an attacker
/// needs in order to reverse it too.
/// </para>
/// <para>
/// So the honest statement of the guarantee: this protects a stolen database
/// file. It does not protect a compromised host. Someone who can read the
/// database <em>and</em> the key material <em>and</em> run as the service
/// account has the passwords, and no arrangement of this shape changes that.
/// </para>
/// <para>
/// The previous product's leak is worth being precise about, because the wrong
/// lesson from it would be "encryption failed". It did not. A password was
/// serialised into a free-text JSON field that the encryption did not cover,
/// and nothing in the product said so. What failed was the scope of the
/// protection and the silence about it — which is why <see cref="Secret"/>
/// makes the value impossible to write down by accident, and why this port
/// exists at all rather than a call to a crypto API scattered at each use.
/// </para>
/// </remarks>
public interface ISecretProtector
{
    /// <summary>Protects a secret for storage.</summary>
    /// <remarks>
    /// Returns a string because that is what goes in a database column. It is
    /// ciphertext, not a value: it is meaningless without the key material, and
    /// it must never be shown to anyone as though it were a password.
    /// </remarks>
    string Protect(Secret secret);

    /// <summary>Recovers a protected secret.</summary>
    /// <exception cref="SecretUnprotectException">
    /// If the value cannot be recovered — most often because the key material
    /// was lost while the database was kept.
    /// </exception>
    Secret Unprotect(string protectedValue);
}

/// <summary>A stored secret could not be recovered.</summary>
/// <remarks>
/// Its own type because the operator response is specific and nothing else
/// implies it: the database survived and the keys did not, so the stored
/// passwords are gone and have to be entered again. Reporting this as a
/// generic failure would send someone looking at vCenter, which is working.
/// </remarks>
public sealed class SecretUnprotectException : Exception
{
    public SecretUnprotectException(string message)
        : base(message)
    {
    }

    public SecretUnprotectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SecretUnprotectException()
        : base("A stored secret could not be recovered.")
    {
    }
}
