using System.Net.Sockets;
using EnterpriseObservatory.Application.Reporting;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace EnterpriseObservatory.Host.AllInOne.Mail;

/// <summary>
/// Sends mail through MailKit's SMTP client.
/// </summary>
/// <remarks>
/// <para>
/// The only implementation of <see cref="IMailSender"/> in the product, the
/// way <c>DataProtectionSecretProtector</c> is the only <c>ISecretProtector</c>:
/// the application layer defines the port, this composition-root-adjacent
/// class is the one place that knows which library answers it.
/// </para>
/// <para>
/// Bounded by a timeout on the connection and on the whole send, because an
/// unreachable relay must fail the dispatch pass in seconds, not hang the
/// scheduler's single worker thread until the next process restart.
/// </para>
/// </remarks>
public sealed class MailKitMailSender : IMailSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<MailSendResult> SendAsync(
        SmtpSettings settings, OutgoingMail mail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mail);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var client = new SmtpClient();

        try
        {
            await client
                .ConnectAsync(settings.Host, settings.Port, SecureSocketOptionsFor(settings.TlsMode), timeout.Token)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(settings.Username))
            {
                await client
                    .AuthenticateAsync(settings.Username, settings.Password.Reveal(), timeout.Token)
                    .ConfigureAwait(false);
            }

            await client.SendAsync(ToMime(settings, mail), timeout.Token).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, timeout.Token).ConfigureAwait(false);

            return MailSendResult.Ok($"Sent to {mail.To.Count} recipient(s).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout fired, not the caller's token -- reported as an
            // ordinary failure rather than left to propagate as a
            // cancellation the caller never asked for.
            return MailSendResult.Failed(
                $"The relay at {settings.Host}:{settings.Port} did not respond in time.",
                MailFailureKind.TimedOut);
        }
        catch (AuthenticationException)
        {
            return MailSendResult.Failed("The relay rejected the username or password.", MailFailureKind.AuthenticationFailed);
        }
        catch (SslHandshakeException ex)
        {
            return MailSendResult.Failed($"The TLS handshake with the relay failed: {ex.Message}", MailFailureKind.TlsFailed);
        }
        catch (SmtpCommandException ex)
        {
            // The relay's own words, which is what an operator needs to fix
            // "550 relaying denied" or a bad from-address -- a generic
            // "send failed" would send them looking at the wrong thing. Kept
            // out of the client-facing MailTestView by EmailApi, which uses
            // FailureKind instead; this Detail is for the server log and the
            // subscription's own failure record.
            return MailSendResult.Failed($"The relay refused the message: {ex.Message}");
        }
        catch (SmtpProtocolException ex)
        {
            return MailSendResult.Failed($"The relay's response could not be understood: {ex.Message}");
        }
        catch (SocketException ex)
        {
            return MailSendResult.Failed($"Could not reach {settings.Host}:{settings.Port} ({ex.Message}).", MailFailureKind.ConnectionFailed);
        }
        catch (System.IO.IOException ex)
        {
            return MailSendResult.Failed($"The connection to the relay was lost: {ex.Message}", MailFailureKind.ConnectionFailed);
        }
        catch (NotSupportedException ex)
        {
            // The relay does not offer STARTTLS, or none of its AUTH
            // mechanisms are ones MailKit can use -- a configuration
            // mismatch, not a bug, and MailKit reports it as this rather
            // than one of the exceptions above.
            return MailSendResult.Failed($"The relay does not support what this connection needs: {ex.Message}");
        }
        catch (MimeKit.ParseException ex)
        {
            // A malformed address or message -- from-address, a recipient,
            // or the content itself. Should be caught by validation before
            // this point, but a library-level parse failure must still come
            // back as a declined send, not an unhandled exception that
            // leaves the caller's subscription looking like it silently
            // succeeded.
            return MailSendResult.Failed($"The message could not be built: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catch-all: anything this library or its transport throws that
            // was not one of the specific cases above must still come back
            // as a MailSendResult.Failed, never escape as an exception. An
            // uncaught exception here previously propagated out of
            // ReportDispatchService.SendOneAsync after the subscription had
            // already been MarkDispatched, so it showed as sent with no
            // error recorded anywhere.
            return MailSendResult.Failed($"The message could not be sent: {ex.Message}");
        }
    }

    private static SecureSocketOptions SecureSocketOptionsFor(SmtpTlsMode mode) => mode switch
    {
        SmtpTlsMode.None => SecureSocketOptions.None,
        SmtpTlsMode.StartTls => SecureSocketOptions.StartTls,
        SmtpTlsMode.Implicit => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private static MimeMessage ToMime(SmtpSettings settings, OutgoingMail mail)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.FromAddress));

        foreach (var recipient in mail.To)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        message.Subject = mail.Subject;

        var body = new BodyBuilder { TextBody = mail.BodyText };

        foreach (var attachment in mail.Attachments)
        {
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }

        message.Body = body.ToMessageBody();
        return message;
    }
}
