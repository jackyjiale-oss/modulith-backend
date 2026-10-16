using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// Delivers rendered messages through one <see cref="NotificationChannel"/>; the delivery worker uses the one registered for a
/// delivery's channel (none: the delivery is dead-lettered with <c>channel_unavailable</c>). Resolved in the worker's scope for each
/// delivery, so a channel may stage rows in the module's unit of work: the worker saves them together with the delivery's outcome.
/// </summary>
internal interface INotificationChannel
{
    NotificationChannel Channel { get; }

    /// <summary>
    /// Delivers <paramref name="work"/> and reports how it went. An expected failure is a result, not an exception; an exception is
    /// treated as a transient failure (<c>channel_error</c>). Never logs or stores an address, a link or the message bodies.
    /// </summary>
    Task<ChannelSendResult> SendAsync(DeliveryWork work, CancellationToken cancellationToken);
}
