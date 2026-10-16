using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Admin.Deliveries.List;

/// <summary>One delivery as an administrator sees it. The columns follow this constructor, which Dapper binds by position.</summary>
/// <param name="Id">The delivery id; the id <c>POST admin/notifications/deliveries/{id}/retry</c> takes.</param>
/// <param name="NotificationId">The notification the delivery belongs to.</param>
/// <param name="TypeCode">The notification type, for example <c>auth.password_changed</c>.</param>
/// <param name="RecipientUserId">The user the notification is for.</param>
/// <param name="Channel">How it is delivered.</param>
/// <param name="Destination">
/// The address, masked by <see cref="DestinationMask"/> (<c>a****@example.com</c>); null for an in-app delivery. The real address is
/// never part of an administrator's response.
/// </param>
/// <param name="Status">Where the delivery is in its life.</param>
/// <param name="AttemptCount">The attempts started so far.</param>
/// <param name="NextAttemptAt">When the next attempt is due (UTC); null unless <see cref="DeliveryStatus.Pending"/>.</param>
/// <param name="LastError">A reason code such as <c>smtp_550</c>, never free text; null when the last attempt did not fail.</param>
/// <param name="CreatedAt">When the delivery was created (UTC).</param>
/// <param name="SentAt">When it was sent (UTC); null until then.</param>
/// <param name="DeadLetteredAt">When it was given up (UTC); null unless <see cref="DeliveryStatus.DeadLettered"/>.</param>
internal sealed record DeliveryResponse(
    Guid Id,
    Guid NotificationId,
    string TypeCode,
    Guid RecipientUserId,
    NotificationChannel Channel,
    string? Destination,
    DeliveryStatus Status,
    int AttemptCount,
    DateTime? NextAttemptAt,
    string? LastError,
    DateTime CreatedAt,
    DateTime? SentAt,
    DateTime? DeadLetteredAt);
