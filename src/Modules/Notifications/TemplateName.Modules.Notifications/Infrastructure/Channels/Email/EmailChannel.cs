using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Deliveries;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Email;

/// <summary>
/// Delivers email: builds the <see cref="EmailMessage"/> from the delivery's destination and the rendered subject, text and HTML, hands
/// it to the <see cref="IEmailTransport"/>, and turns an <see cref="EmailSendException"/> into a permanent or transient result with the
/// transport's reason code. A reason that is not a short code becomes <c>unknown</c>, so a server's reply can never reach
/// <c>LastError</c> or a log. Any other exception goes to the delivery worker, which counts it as a transient <c>channel_error</c>.
/// Nothing is stored or logged here: the message may hold a decrypted single-use link.
/// </summary>
internal sealed class EmailChannel(IEmailTransport transport) : INotificationChannel
{
    /// <summary>The reason of an email delivery without an address (the domain refuses one; this is a second line of defence).</summary>
    internal const string InvalidAddress = "invalid_address";

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<ChannelSendResult> SendAsync(DeliveryWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (string.IsNullOrWhiteSpace(work.Destination))
        {
            return ChannelSendResult.Permanent(InvalidAddress);
        }

        var message = new EmailMessage(work.Destination, work.Message.Subject, work.Message.TextBody, work.Message.HtmlBody);
        try
        {
            await transport.SendAsync(message, cancellationToken);
            return ChannelSendResult.Sent;
        }
        catch (EmailSendException exception)
        {
            var reason = ChannelSendResult.IsReasonCode(exception.Reason) ? exception.Reason : DeliveryDispatcher.UnknownReason;
            return exception.IsPermanent ? ChannelSendResult.Permanent(reason) : ChannelSendResult.Transient(reason);
        }
    }
}
