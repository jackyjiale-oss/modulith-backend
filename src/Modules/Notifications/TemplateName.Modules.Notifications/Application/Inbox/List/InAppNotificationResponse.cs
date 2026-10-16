using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Inbox.List;

/// <summary>One notification of the caller's inbox. The owner is always the caller, so it carries no user id.</summary>
/// <param name="Id">The notification id; the id <c>POST notifications/inbox/{id}/read</c> takes.</param>
/// <param name="TypeCode">The notification type, for example <c>auth.password_changed</c>.</param>
/// <param name="Category">What it is about.</param>
/// <param name="Title">The rendered title, in the language the recipient had saved when it was created.</param>
/// <param name="Body">The rendered body.</param>
/// <param name="CreatedAt">When it was created (UTC).</param>
/// <param name="ReadAt">When it was first read (UTC); null while unread.</param>
internal sealed record InAppNotificationResponse(
    Guid Id,
    string TypeCode,
    NotificationCategory Category,
    string Title,
    string Body,
    DateTime CreatedAt,
    DateTime? ReadAt);
