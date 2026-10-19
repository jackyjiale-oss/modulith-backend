using Microsoft.AspNetCore.SignalR;

namespace TemplateName.Modules.Notifications.Infrastructure.Hubs;

/// <summary>
/// The notification hub at <see cref="Path"/> (D12, ADR 0021). It only pushes: clients receive <see cref="NotificationReceived"/> and can
/// call nothing, because the hub declares no methods. Each connection belongs to the user its authentication names
/// (<see cref="SubClaimUserIdProvider"/>), and <c>SignalRInAppNotificationPusher</c> sends to all of that user's connections.
/// </summary>
internal sealed class NotificationsHub : Hub
{
    /// <summary>Where the hub is mapped, at the application root (outside <c>/api/v1</c>).</summary>
    public const string Path = "/hubs/notifications";

    /// <summary>The server event that carries a new in-app notification, as the inbox lists it (<c>InAppNotificationPushed</c>).</summary>
    public const string NotificationReceived = "notificationReceived";
}
