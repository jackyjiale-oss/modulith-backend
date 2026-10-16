using Microsoft.AspNetCore.SignalR;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Infrastructure.Hubs;

/// <summary>
/// Pushes a saved in-app notification to every connection of its user on this instance (<see cref="NotificationsHub.NotificationReceived"/>).
/// A user with no connection here receives nothing, which is fine: the inbox is the source of truth, and without a backplane (D13, Plan 5)
/// connections on other instances are not reached. Exceptions reach <c>InAppChannel</c>, which logs them and carries on (best effort).
/// </summary>
internal sealed class SignalRInAppNotificationPusher(IHubContext<NotificationsHub> hub) : IInAppNotificationPusher
{
    public Task PushAsync(Guid userId, InAppNotificationPushed payload, CancellationToken cancellationToken)
        => hub.Clients.User(userId.ToString()).SendAsync(NotificationsHub.NotificationReceived, payload, cancellationToken);
}
