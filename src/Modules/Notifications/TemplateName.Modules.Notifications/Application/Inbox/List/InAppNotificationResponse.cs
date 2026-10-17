using System.Text;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Inbox.List;

/// <summary>
/// One notification of the caller's inbox. The owner is always the caller, so it carries no user id. <c>ToString</c> prints the id,
/// the type, the category and the times, not the rendered texts (the JSON response is unaffected).
/// </summary>
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
    DateTime? ReadAt)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id)
            .Append(", TypeCode = ").Append(TypeCode)
            .Append(", Category = ").Append(Category)
            .Append(", CreatedAt = ").Append(CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Append(", ReadAt = ").Append(ReadAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }
}
