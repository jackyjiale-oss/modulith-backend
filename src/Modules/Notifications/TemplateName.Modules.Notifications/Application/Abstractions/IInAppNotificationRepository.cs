using TemplateName.Modules.Notifications.Domain.InApp;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

internal interface IInAppNotificationRepository
{
    /// <summary>The tracked in-app notification, only when it belongs to <paramref name="userId"/>; <see langword="null"/> otherwise.</summary>
    Task<InAppNotification?> GetForUserAsync(Guid id, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks every unread notification of <paramref name="userId"/> created at or before <paramref name="now"/> as read, in one
    /// statement; rows created later stay unread. Bypasses the change tracker. Returns the number of rows marked.
    /// </summary>
    Task<int> MarkAllReadAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
}
