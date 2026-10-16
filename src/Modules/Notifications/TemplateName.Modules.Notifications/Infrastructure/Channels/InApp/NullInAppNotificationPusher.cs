using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.InApp;

/// <summary>Pushes nothing: the in-app pusher until the SignalR hub exists. Clients see new notifications when they fetch the inbox.</summary>
internal sealed class NullInAppNotificationPusher : IInAppNotificationPusher
{
    public Task PushAsync(Guid userId, InAppNotificationPushed payload, CancellationToken cancellationToken) => Task.CompletedTask;
}
