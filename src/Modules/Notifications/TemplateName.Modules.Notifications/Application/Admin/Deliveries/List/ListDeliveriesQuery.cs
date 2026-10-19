using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Admin.Deliveries.List;

/// <summary>
/// A page of deliveries, newest first by default, optionally only those with one status, channel, notification type or recipient. A
/// blank <paramref name="TypeCode"/> is no filter; a code no notification type has simply matches nothing.
/// </summary>
/// <param name="Page">The paging part: page size, cursor, sort (<c>createdAt</c> only) and the opt-in total count.</param>
/// <param name="Status">Only deliveries in this status.</param>
/// <param name="Channel">Only deliveries through this channel.</param>
/// <param name="TypeCode">Only deliveries of notifications of this type, for example <c>auth.password_changed</c>.</param>
/// <param name="RecipientUserId">Only deliveries of notifications for this user.</param>
internal sealed record ListDeliveriesQuery(
    CursorPageRequest Page,
    DeliveryStatus? Status,
    NotificationChannel? Channel,
    string? TypeCode,
    Guid? RecipientUserId) : IQuery<CursorPage<DeliveryResponse>>;
