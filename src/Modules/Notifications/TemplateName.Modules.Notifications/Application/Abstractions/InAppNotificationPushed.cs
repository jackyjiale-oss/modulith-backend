using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>What a live push carries: the saved in-app notification as the inbox lists it (its id is the delivery id).</summary>
internal sealed record InAppNotificationPushed(Guid Id, string TypeCode, NotificationCategory Category, string Title, string Body, DateTime CreatedAt);
