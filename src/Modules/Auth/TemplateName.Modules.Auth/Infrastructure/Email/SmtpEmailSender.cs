using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.Email;

/// <summary>
/// Sends email through SMTP with MailKit. Every call opens its own connection, so the sender is safe to share. It throws when sending
/// fails, so the outbox retries. It logs only that an email was sent or failed: never the body and never the recipient, who is
/// personal data.
/// </summary>
internal sealed partial class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var mime = Build(message, settings);

        try
        {
            using var client = new SmtpClient { Timeout = (int)settings.Timeout.TotalMilliseconds };

            await client.ConnectAsync(
                settings.Host,
                settings.Port,
                settings.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                cancellationToken);

            if (settings.Username.Length > 0)
            {
                await client.AuthenticateAsync(settings.Username, settings.Password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await DisconnectQuietlyAsync(client, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailed(logger, exception.GetType().Name);
            throw;
        }

        LogSent(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "An email was sent")]
    private static partial void LogSent(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending an email failed with {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);

    /// <summary>
    /// The message is already accepted when this runs, so a failure to say goodbye must not fail the send: the outbox would retry and
    /// the recipient would get the email twice. Disposing the client closes the connection anyway.
    /// </summary>
    private static async Task DisconnectQuietlyAsync(SmtpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception)
        {
            // Deliberately ignored, see the summary.
        }
    }

    private static MimeMessage Build(EmailMessage message, EmailOptions settings)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }
}
