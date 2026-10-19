using System.Text;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// What a live push carries: the saved in-app notification as the inbox lists it (its id is the delivery id). <c>ToString</c> prints
/// the id, the type, the category and the time, not the rendered texts.
/// </summary>
internal sealed record InAppNotificationPushed(Guid Id, string TypeCode, NotificationCategory Category, string Title, string Body, DateTime CreatedAt)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id)
            .Append(", TypeCode = ").Append(TypeCode)
            .Append(", Category = ").Append(Category)
            .Append(", CreatedAt = ").Append(CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }
}
