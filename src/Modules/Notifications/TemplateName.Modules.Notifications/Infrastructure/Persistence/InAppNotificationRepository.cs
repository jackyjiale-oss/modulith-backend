using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.InApp;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class InAppNotificationRepository(NotificationsDbContext context) : IInAppNotificationRepository
{
    public Task<InAppNotification?> GetForUserAsync(Guid id, Guid userId, CancellationToken cancellationToken)
        => context.Set<InAppNotification>().SingleOrDefaultAsync(notification => notification.Id == id && notification.UserId == userId, cancellationToken);

    // One UPDATE on the unread rows: the database decides which rows it touches, however many arrive meanwhile (those created after
    // now are left alone). ExecuteUpdate bypasses the change tracker, so a row this context already holds keeps its in-memory state.
    public Task<int> MarkAllReadAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var readAt = now.UtcDateTime;

        return context.Set<InAppNotification>()
            .Where(notification => notification.UserId == userId && notification.ReadAt == null && notification.CreatedAt <= readAt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.ReadAt, readAt), cancellationToken);
    }
}
