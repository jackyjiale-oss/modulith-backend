using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Infrastructure.Hubs;

/// <summary>
/// Pushes a saved in-app notification to every connection of its user (<see cref="NotificationsHub.NotificationReceived"/>): on this
/// instance only with SignalR's in-memory lifetime manager, on every instance with the Redis backplane (<c>Caching:Provider = redis</c>,
/// D13). A user with no connection receives nothing, which is fine: the inbox is the source of truth. The push is best effort (ADR 0021):
/// a send that fails (with the backplane, a Redis that is down fails it at once and one that does not answer after its command timeout)
/// is logged at Warning with its exception type only and ignored, so it never reaches the delivery. Only the caller's own cancellation
/// propagates (<c>InAppChannel</c> cancels a push it has stopped waiting for).
/// </summary>
internal sealed partial class SignalRInAppNotificationPusher(IHubContext<NotificationsHub> hub, ILogger<SignalRInAppNotificationPusher> logger)
    : IInAppNotificationPusher
{
    public async Task PushAsync(Guid userId, InAppNotificationPushed payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            await hub.Clients.User(userId.ToString()).SendAsync(NotificationsHub.NotificationReceived, payload, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The type only: a Redis exception's message names the user's channel.
            LogSendFailed(logger, payload.Id, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending in-app notification {InAppNotificationId} to the hub failed ({ExceptionType}); its user's open connections did not receive it live and see it on the next fetch")]
    private static partial void LogSendFailed(ILogger logger, Guid inAppNotificationId, string exceptionType);
}
