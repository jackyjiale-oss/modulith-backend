using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

internal interface INotificationRepository
{
    /// <summary>Stages a new notification with its deliveries.</summary>
    void Add(Notification notification);

    /// <summary>
    /// The delivery with <paramref name="deliveryId"/>, tracked, with its notification (and the notification's other deliveries) loaded
    /// into the same context; <see langword="null"/> when there is none.
    /// </summary>
    Task<Delivery?> GetDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken);
}
