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
            return MailSendResult.Failed($"The relay at {settings.Host}:{settings.Port} did not respond in time.");
        }
        catch (AuthenticationException)
        {
            return MailSendResult.Failed("The relay rejected the username or password.");
        }
        catch (SmtpCommandException ex)
        {
            // The relay's own words, which is what an operator needs to fix
            // "550 relaying denied" or a bad from-address -- a generic
            // "send failed" would send them looking at the wrong thing.
            return MailSendResult.Failed($"The relay refused the message: {ex.Message}");
        }
        catch (SmtpProtocolException ex)
        {
            return MailSendResult.Failed($"The relay's response could not be understood: {ex.Message}");
        }
        catch (SocketException ex)
        {
            return MailSendResult.Failed($"Could not reach {settings.Host}:{settings.Port} ({ex.Message}).");
        }
        catch (System.IO.IOException ex)
        {
            return MailSendResult.Failed($"The connection to the relay was lost: {ex.Message}");
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
