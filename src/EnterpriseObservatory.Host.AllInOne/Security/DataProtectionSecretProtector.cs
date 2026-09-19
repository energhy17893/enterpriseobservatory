using System.Security.Cryptography;
using EnterpriseObservatory.Application.Security;
using Microsoft.AspNetCore.DataProtection;

// The framework has its own type called Secret, and it is not this one. Named
// explicitly rather than resolved by using-order, because the two are close
// enough in purpose that a silent bind to the wrong one would compile.
using Secret = EnterpriseObservatory.Application.Security.Secret;

namespace EnterpriseObservatory.Host.AllInOne.Security;

/// <summary>
/// Protects stored credentials with ASP.NET Core data protection.
/// </summary>
/// <remarks>
/// <para>
/// The framework's own mechanism rather than a hand-rolled one. Not because
/// calling AES is hard, but because the parts that are actually hard — where
/// the key lives, how it is rotated, how ciphertext written by last year's key
/// is still readable — are the parts a hand-rolled version quietly gets wrong,
/// and they are exactly the parts that decide whether this works in two years.
/// </para>
/// <para>
/// The purpose string is versioned. It is mixed into key derivation, so
/// ciphertext written under one purpose cannot be read under another: a stored
/// vCenter password cannot be replayed through some future endpoint that
/// protects something else. Changing the version makes every existing value
/// unreadable, which is why it is a deliberate string and not a constant
/// somebody will tidy up.
/// </para>
/// </remarks>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    /// <summary>What these keys are for. Changing it orphans every stored secret.</summary>
    public const string Purpose = "EnterpriseObservatory.SourceConnection.Password.v1";

    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(Secret secret) =>
        secret.IsEmpty ? string.Empty : _protector.Protect(secret.Reveal());

    public Secret Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);

        if (protectedValue.Length == 0)
        {
            // Never set, as opposed to set and unreadable. The caller has to be
            // able to tell those apart, so an empty column stays empty rather
            // than becoming an error about cryptography.
            return Secret.Empty;
        }

        try
        {
            return Secret.From(_protector.Unprotect(protectedValue));
        }
        catch (CryptographicException ex)
        {
            // Almost always one thing: the database was restored somewhere the
            // key ring was not. Said in those terms, because the alternative is
            // an operator reading "padding is invalid" and going to look at
            // vCenter, which is working perfectly.
            throw new SecretUnprotectException(
                "A stored password could not be decrypted. The key material that protects it is " +
                "missing or belongs to a different installation — this happens when a database is " +
                "restored without the keys beside it. The passwords have to be entered again.",
                ex);
        }
    }
}
