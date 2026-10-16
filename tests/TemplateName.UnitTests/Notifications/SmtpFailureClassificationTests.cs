using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Email;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// How <c>SmtpEmailTransport</c> turns a MailKit, socket or I/O failure into a reason code, permanent only when retrying cannot help.
/// The integration tests cover the same rules against a real server (Mailpit).
/// </summary>
public sealed class SmtpFailureClassificationTests
{
    private const string Address = "alice@example.com";

    [Theory]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, true, "smtp_550")]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxNameNotAllowed, true, "smtp_553")]
    [InlineData(SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxUnavailable, true, "smtp_550")]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxBusy, false, "smtp_450")]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.InsufficientStorage, false, "smtp_452")]
    [InlineData(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, false, "smtp_554")]
    [InlineData(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.ServiceNotAvailable, false, "smtp_421")]
    public void Smtp_replies_keep_their_code_and_only_a_refused_mailbox_is_permanent(
        SmtpErrorCode errorCode, SmtpStatusCode statusCode, bool isPermanent, string reason)
    {
        // The server's text quotes the address; it must not survive the classification.
        var command = new SmtpCommandException(errorCode, statusCode, new MailboxAddress(null, Address), $"5.1.1 <{Address}>: Recipient address rejected");

        var failure = SmtpEmailTransport.Classify(command);

        failure.IsPermanent.ShouldBe(isPermanent);
        failure.Reason.ShouldBe(reason);
        failure.Message.ShouldBe(reason);
        failure.InnerException.ShouldBeNull();
    }

    [Fact]
    public void Network_and_protocol_failures_are_transient_short_codes()
    {
        SmtpEmailTransport.Classify(new SocketException((int)SocketError.ConnectionRefused)).Reason.ShouldBe("connection_failed");
        SmtpEmailTransport.Classify(new IOException("reset", new SocketException((int)SocketError.ConnectionReset))).Reason.ShouldBe("connection_failed");
        SmtpEmailTransport.Classify(new SocketException((int)SocketError.TimedOut)).Reason.ShouldBe("timeout");
        SmtpEmailTransport.Classify(new TimeoutException($"No answer for {Address}")).Reason.ShouldBe("timeout");
        SmtpEmailTransport.Classify(new OperationCanceledException()).Reason.ShouldBe("timeout");
        SmtpEmailTransport.Classify(new SmtpProtocolException($"Unexpected reply about {Address}")).Reason.ShouldBe("smtp_error");
        SmtpEmailTransport.Classify(new ServiceNotConnectedException()).Reason.ShouldBe("smtp_error");
    }

    [Fact]
    public void Any_other_failure_is_transient_and_named_by_its_type_in_snake_case()
    {
        var failures = new Exception[]
        {
            new SslHandshakeException($"Certificate of {Address}"),
            new AuthenticationException("bad credentials"),
            new IOException("disk"),
            new InvalidOperationException("odd"),
        };

        failures.Select(SmtpEmailTransport.Classify).Select(failure => (failure.IsPermanent, failure.Reason)).ShouldBe(
        [
            (false, "ssl_handshake_exception"),
            (false, "authentication_exception"),
            (false, "io_exception"),
            (false, "invalid_operation_exception"),
        ]);
    }
}
