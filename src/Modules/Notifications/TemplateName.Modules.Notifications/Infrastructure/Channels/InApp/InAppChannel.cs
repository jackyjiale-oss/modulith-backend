using Microsoft.Extensions.Logging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Deliveries;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.InApp;

/// <summary>
/// Delivers in-app: stages the inbox row (<see cref="InAppNotification"/>, keyed by the delivery id) from the rendered title and body, and
/// answers <see cref="ChannelSendResult.Sent"/>. The delivery worker saves the row with the delivery's outcome, so it exists exactly once
/// (D14). Once that save has succeeded, the row is pushed to the user's open connections, best effort: a failed push is logged at
/// Warning (its exception type only) and ignored, because the inbox is the source of truth.
/// </summary>
internal sealed partial class InAppChannel(
    IInAppNotificationRepository inAppNotifications,
    IInAppNotificationPusher pusher,
    DeliverySaveCallbacks saveCallbacks,
    TimeProvider timeProvider,
    ILogger<InAppChannel> logger)
    : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.InApp;

    public Task<ChannelSendResult> SendAsync(DeliveryWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var notification = InAppNotification.Create(
            work.DeliveryId,
            work.RecipientUserId,
            work.NotificationId,
            work.TypeCode,
            work.Category,
            work.Message.Subject,
            work.Message.TextBody,
            timeProvider.GetUtcNow());
        inAppNotifications.Add(notification);
        saveCallbacks.Add(callbackCancellationToken => PushAsync(notification, callbackCancellationToken));

        return Task.FromResult(ChannelSendResult.Sent);
    }

    private async Task PushAsync(InAppNotification notification, CancellationToken cancellationToken)
    {
        var payload = new InAppNotificationPushed(
            notification.Id, notification.TypeCode, notification.Category, notification.Title, notification.Body, notification.CreatedAt);
        try
        {
            await pusher.PushAsync(notification.UserId, payload, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPushFailed(logger, notification.Id, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The live push of in-app notification {InAppNotificationId} failed ({ExceptionType}); the user sees it on the next fetch")]
    private static partial void LogPushFailed(ILogger logger, Guid inAppNotificationId, string exceptionType);
}
