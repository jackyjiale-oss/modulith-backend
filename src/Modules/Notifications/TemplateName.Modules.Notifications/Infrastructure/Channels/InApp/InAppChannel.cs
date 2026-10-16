using Microsoft.Extensions.Logging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Deliveries;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.InApp;

/// <summary>
/// Delivers in-app: stages the inbox row (<see cref="InAppNotification"/>, keyed by the delivery id) from the rendered title and body, and
/// answers <see cref="ChannelSendResult.Sent"/>. The delivery worker saves the row with the delivery's outcome, so it exists exactly once
/// (D14). Once that save has succeeded, the row is pushed to the user's open connections, best effort and bounded: a failed push is
/// logged at Warning (its exception type only) and ignored, because the inbox is the source of truth, and a push that has not finished
/// after <see cref="PushTimeout"/> (on the injected <see cref="TimeProvider"/>) is abandoned with a Warning, so a hub client that stops
/// reading cannot stall the delivery worker.
/// </summary>
internal sealed partial class InAppChannel(
    IInAppNotificationRepository inAppNotifications,
    IInAppNotificationPusher pusher,
    DeliverySaveCallbacks saveCallbacks,
    TimeProvider timeProvider,
    ILogger<InAppChannel> logger)
    : INotificationChannel
{
    /// <summary>How long the worker waits for one live push before it moves on; the push is cancelled then.</summary>
    internal static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(5);

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
        using var pushCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? push = null;
        try
        {
            push = pusher.PushAsync(notification.UserId, payload, pushCancellation.Token);

            // Bounded even when the pusher ignores its token: the wait ends at the timeout whatever the push does.
            await push.WaitAsync(PushTimeout, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            await pushCancellation.CancelAsync();

            // The abandoned push may still fail later; observe it so its exception is not reported as unobserved.
            _ = push?.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            LogPushTimedOut(logger, notification.Id, PushTimeout.TotalSeconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPushFailed(logger, notification.Id, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The live push of in-app notification {InAppNotificationId} failed ({ExceptionType}); the user sees it on the next fetch")]
    private static partial void LogPushFailed(ILogger logger, Guid inAppNotificationId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The live push of in-app notification {InAppNotificationId} timed out after {TimeoutSeconds} s and was abandoned; the user sees it on the next fetch")]
    private static partial void LogPushTimedOut(ILogger logger, Guid inAppNotificationId, double timeoutSeconds);
}
