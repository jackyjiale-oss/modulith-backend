using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class NotificationRepository(NotificationsDbContext context) : INotificationRepository
{
    public void Add(Notification notification) => context.Set<Notification>().Add(notification);

    // The notification is loaded with all its deliveries, so the delivery is tracked together with its notification and siblings.
    public async Task<Delivery?> GetDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var notification = await context.Set<Notification>()
            .Include(candidate => candidate.Deliveries)
            .AsSingleQuery()
            .SingleOrDefaultAsync(candidate => candidate.Deliveries.Any(delivery => delivery.Id == deliveryId), cancellationToken);

        return notification?.Deliveries.Single(delivery => delivery.Id == deliveryId);
    }
}
