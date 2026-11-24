using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Sms;

/// <summary>
/// Delivers the <see cref="NotificationChannel.Sms"/> deliveries: the rendered one-line text to the delivery's number, through
/// <see cref="ISmsTransport"/>. A destination that is not a number in E.164 is <c>invalid_number</c>, permanent. The provider's message
/// id comes back with the sent result, so the delivery keeps it; an id that is not one is dropped rather than failing a sent text.
/// Registered only while <c>Notifications:Sms:Enabled</c> is true.
/// </summary>
internal sealed class SmsChannel(ISmsTransport transport) : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.Sms;

    public async Task<ChannelSendResult> SendAsync(DeliveryWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!SmsDestination.IsValid(work.Destination))
        {
            return ChannelSendResult.Permanent("invalid_number");
        }

        try
        {
            var messageId = await transport.SendAsync(new SmsMessage(work.DeliveryId, work.Destination, work.Message.TextBody), cancellationToken);

            // The text is out: nothing after this may throw, or the worker would retry and text again. An id that is not one is dropped.
            return ChannelSendResult.IsProviderMessageId(messageId) ? ChannelSendResult.SentAs(messageId) : ChannelSendResult.Sent;
        }
        catch (SmsSendException failure)
        {
            return failure.IsPermanent ? ChannelSendResult.Permanent(failure.Reason) : ChannelSendResult.Transient(failure.Reason);
        }
    }
}
