using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Email;

/// <summary>
/// Sends email through SMTP with MailKit (<see cref="EmailOptions"/>). Every call opens its own connection, so the transport is safe to
/// share. A failure throws <see cref="EmailSendException"/> with a short reason code, classified so the delivery worker knows whether
/// to retry:
/// <list type="bullet">
/// <item>an address <see cref="MailboxAddress"/> cannot parse: permanent <c>invalid_address</c> (nothing is sent);</item>
/// <item>the server refuses the recipient or the sender with a 5xx reply: permanent <c>smtp_{code}</c>, such as <c>smtp_550</c>;</item>
/// <item>any other SMTP reply: transient <c>smtp_{code}</c>, such as <c>smtp_451</c>;</item>
/// <item>a timeout: transient <c>timeout</c>; a socket error (refused, reset, unreachable): transient <c>connection_failed</c>; a broken
/// SMTP conversation: transient <c>smtp_error</c>; anything else: transient, the exception type in snake case
/// (<c>ssl_handshake_exception</c>).</item>
/// </list>
/// The reason never holds the server's reply, the address or a link, and the original exception is not attached, because its message
/// can quote them. Logs say only that an email was sent, or the reason and the exception type: never the recipient (personal data) or
/// the bodies. Cancelling the token throws <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed partial class SmtpEmailTransport(IOptions<EmailOptions> options, ILogger<SmtpEmailTransport> logger) : IEmailTransport
{
    internal const string InvalidAddress = "invalid_address";
    internal const string TimedOut = "timeout";
    internal const string ConnectionFailed = "connection_failed";
    internal const string SmtpError = "smtp_error";

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var settings = options.Value;
        if (!MailboxAddress.TryParse(message.To, out var recipient))
        {
            LogInvalidAddress(logger);
            throw new EmailSendException(isPermanent: true, InvalidAddress);
        }

        var mime = Build(message, recipient, settings);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = Classify(exception);
            LogFailed(logger, failure.Reason, failure.IsPermanent, exception.GetType().Name);
            throw failure;
        }

        LogSent(logger);
    }

    /// <summary>Maps a MailKit, socket or I/O failure to a permanent or transient reason code (see the class summary).</summary>
    internal static EmailSendException Classify(Exception exception)
    {
        if (exception is SmtpCommandException command)
        {
            var code = (int)command.StatusCode;
            var refusedMailbox = command.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.SenderNotAccepted;
            return new EmailSendException(isPermanent: refusedMailbox && code is >= 500 and <= 599, $"smtp_{code}");
        }

        if (IsTimeout(exception))
        {
            return new EmailSendException(isPermanent: false, TimedOut);
        }

        if (exception is SocketException || exception.InnerException is SocketException)
        {
            return new EmailSendException(isPermanent: false, ConnectionFailed);
        }

        if (exception is SmtpProtocolException or ServiceNotConnectedException)
        {
            return new EmailSendException(isPermanent: false, SmtpError);
        }

        var typeName = SnakeCase(exception.GetType().Name);
        return new EmailSendException(isPermanent: false, ChannelSendResult.IsReasonCode(typeName) ? typeName : SmtpError);
    }

    // MailKit reports its own timeout (EmailOptions.Timeout) as a TimeoutException, or as an OperationCanceledException that the
    // caller did not ask for; a socket can also time out.
    private static bool IsTimeout(Exception exception)
        => exception is TimeoutException or OperationCanceledException
            || exception.InnerException is TimeoutException
            || exception is SocketException { SocketErrorCode: SocketError.TimedOut }
            || exception.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut };

    // SslHandshakeException -> ssl_handshake_exception, IOException -> io_exception.
    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];
            if (char.IsUpper(character))
            {
                var startsWord = i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (startsWord)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "An email was sent")]
    private static partial void LogSent(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An email was not sent: its recipient address is not valid (invalid_address)")]
    private static partial void LogInvalidAddress(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending an email failed with {Reason} (permanent: {IsPermanent}, {ExceptionType})")]
    private static partial void LogFailed(ILogger logger, string reason, bool isPermanent, string exceptionType);

    /// <summary>
    /// The message is already accepted when this runs, so a failure to say goodbye must not fail the send: the delivery would be retried
    /// and the recipient would get the email twice. Disposing the client closes the connection anyway.
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

    private static MimeMessage Build(EmailMessage message, MailboxAddress recipient, EmailOptions settings)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.From));
        mime.To.Add(recipient);
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }
}
