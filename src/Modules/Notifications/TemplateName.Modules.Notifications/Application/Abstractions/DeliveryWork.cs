using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// One rendered message the delivery worker hands to an <see cref="INotificationChannel"/>. It lives only in memory for the attempt: an
/// email's <see cref="RenderedMessage"/> may hold a decrypted single-use link, so a channel never stores or logs it (it stores only what
/// it must deliver, such as the in-app title and body, which never contain a secret).
/// </summary>
/// <param name="DeliveryId">The delivery being attempted; the in-app row's key.</param>
/// <param name="NotificationId">The notification the delivery belongs to.</param>
/// <param name="RecipientUserId">The user the message is for.</param>
/// <param name="TypeCode">The notification type code.</param>
/// <param name="Category">The type's category.</param>
/// <param name="Destination">The address an email goes to; <see langword="null"/> for in-app.</param>
/// <param name="Message">The message rendered for the delivery's channel in the notification's culture.</param>
internal sealed record DeliveryWork(
    Guid DeliveryId,
    Guid NotificationId,
    Guid RecipientUserId,
    string TypeCode,
    NotificationCategory Category,
    string? Destination,
    RenderedMessage Message);
