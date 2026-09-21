using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Application.Reporting;

/// <summary>How the connection to the SMTP relay is secured.</summary>
public enum SmtpTlsMode
{
    /// <summary>Plain text. Refused unless explicitly allowed — see <see cref="SmtpSettings.Validate"/>.</summary>
    None,

    /// <summary>Connects in the clear, then upgrades. The default, and what most relays expect on 587.</summary>
    StartTls,

    /// <summary>TLS from the first byte, on its own port — typically 465.</summary>
    Implicit,
}

/// <summary>
/// How this installation sends mail.
/// </summary>
/// <remarks>
/// Stored the same way a <see cref="Collection.SourceConnection"/> is: the
/// password is a <see cref="Secret"/>, encrypted at rest by the same
/// <see cref="ISecretProtector"/>, and never travels back out through the API.
/// There is exactly one of these per installation — unlike vCenter connections,
/// an estate does not have several mail relays — so it is a single row rather
/// than a table keyed by name.
/// </remarks>
public sealed record SmtpSettings
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 587;

    public SmtpTlsMode TlsMode { get; init; } = SmtpTlsMode.StartTls;

    public string FromAddress { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public Secret Password { get; init; } = Secret.Empty;

    /// <summary>
    /// Whether sending credentials over an unencrypted connection was a
    /// deliberate choice, not an oversight.
    /// </summary>
    /// <remarks>
    /// <see cref="SmtpTlsMode.None"/> puts the username and password on the
    /// wire in the clear. That is sometimes correct — a relay reachable only
    /// on a closed management network, mirroring the "over http" allowance
    /// ADR-0015 does not make for vCenter but this product still has to be
    /// deployable into estates that made that call before it arrived — but it
    /// must never be the default a form silently saves.
    /// </remarks>
    public bool AllowUnencrypted { get; init; }

    public DateTimeOffset? PasswordSetUtc { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);

    /// <summary>Every problem with these settings, not just the first. See <see cref="Collection.SourceConnection.Validate"/> for why.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Host))
        {
            problems.Add("A relay host is required.");
        }

        if (Port is < 1 or > 65535)
        {
            problems.Add("The port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(FromAddress) || !MailAddresses.IsValid(FromAddress))
        {
            problems.Add("The from address must be a valid email address.");
        }

        if (TlsMode == SmtpTlsMode.None && !AllowUnencrypted)
        {
            problems.Add(
                "Sending over an unencrypted connection is refused unless explicitly allowed — " +
                "credentials would travel in the clear.");
        }

        return problems;
    }
}

/// <summary>Where the installation's SMTP settings live. One row, not a table.</summary>
public interface ISmtpSettingsStore
{
    /// <summary>The current settings, or an unconfigured default if none were ever saved.</summary>
    SmtpSettings Current { get; }

    /// <summary>
    /// Replaces the settings.
    /// </summary>
    /// <param name="settings">
    /// An empty <see cref="SmtpSettings.Password"/> means "leave the stored
    /// password alone" — the same rule <see cref="ISourceConnectionStore.Update"/>
    /// follows, and for the same reason: nothing can read the stored password
    /// back out, so a form that shows a blank field must not be able to erase
    /// a working credential just by being saved unchanged.
    /// </param>
    void Save(SmtpSettings settings);
}

/// <summary>A minimal, dependency-free address check for the from field and recipients.</summary>
/// <remarks>
/// Not a full RFC 5322 parser — nothing this product does needs one, and a
/// permissive regex that lets through a malformed address fails where it
/// belongs, at the SMTP relay, with a clear rejection. This only catches the
/// obviously wrong: no @, no domain, embedded whitespace.
/// </remarks>
public static class MailAddresses
{
    public static bool IsValid(string address) =>
        !string.IsNullOrWhiteSpace(address) &&
        System.Net.Mail.MailAddress.TryCreate(address.Trim(), out var parsed) &&
        parsed.Address == address.Trim();
}

/// <summary>A file attached to a report mail. The report content supplies these — see <see cref="IReportRenderer"/>.</summary>
public sealed record MailAttachment
{
    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required byte[] Content { get; init; }
}

/// <summary>
/// A mail to send. Named to avoid colliding with <see cref="System.Net.Mail.MailMessage"/>
/// — this is the product's own shape, independent of whichever library sends it.
/// </summary>
public sealed record OutgoingMail
{
    public required IReadOnlyList<string> To { get; init; }

    public required string Subject { get; init; }

    public required string BodyText { get; init; }

    public IReadOnlyList<MailAttachment> Attachments { get; init; } = [];
}

/// <summary>What one send attempt found.</summary>
/// <param name="Succeeded">Whether the relay accepted the mail.</param>
/// <param name="Detail">
/// What happened, for the operator. Never the password, never a stack trace —
/// the SMTP relay's own rejection reason when there is one.
/// </param>
public readonly record struct MailSendResult(bool Succeeded, string Detail)
{
    public static MailSendResult Ok(string detail) => new(true, detail);

    public static MailSendResult Failed(string detail) => new(false, detail);
}

/// <summary>
/// Sends mail through the installation's SMTP relay.
/// </summary>
/// <remarks>
/// <para>
/// A port, like every collector and every store: the application layer knows
/// it can send mail and knows nothing about MailKit, System.Net.Mail, or
/// whatever sends it. See ADR-0001.
/// </para>
/// <para>
/// Expected failures — the relay is unreachable, the credentials are
/// rejected, a recipient is refused — are reported through the returned
/// <see cref="MailSendResult"/>, not thrown. The scheduler that calls this
/// records the result rather than catching an exception, which is what keeps
/// "the last report failed, here is why" a fact on the record instead of a
/// log line nobody was watching. A genuinely unexpected failure — a bug —
/// still throws; it must never be swallowed by this port pretending it was a
/// declined send.
/// </para>
/// </remarks>
public interface IMailSender
{
    Task<MailSendResult> SendAsync(
        SmtpSettings settings, OutgoingMail mail, CancellationToken cancellationToken);
}
