using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// The module's instruments (meter <c>TemplateName.Notifications</c>). Tags carry fixed values only (a type code from the catalog, a
/// channel, a result): never a user id, an address, a link or an error text.
/// </summary>
internal interface INotificationsMetrics
{
    /// <summary>Counts one notification created by the scheduler (<c>notifications.created</c>, tag <c>type</c>).</summary>
    void RecordCreated(string typeCode);

    /// <summary>
    /// Counts one settled delivery attempt (<c>notifications.deliveries</c>, tags <c>channel</c> and <c>result</c>) and records how long
    /// it took (<c>notifications.delivery.duration</c> in milliseconds, tag <c>channel</c>).
    /// </summary>
    void RecordDelivery(NotificationChannel channel, DeliveryResult result, TimeSpan duration);
}
