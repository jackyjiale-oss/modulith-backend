namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// Tells a user's open connections that an in-app notification arrived. Best effort: the inbox row is already saved and is the source
/// of truth, so a failed push only means the client sees it on its next fetch.
/// </summary>
internal interface IInAppNotificationPusher
{
    Task PushAsync(Guid userId, InAppNotificationPushed payload, CancellationToken cancellationToken);
}
