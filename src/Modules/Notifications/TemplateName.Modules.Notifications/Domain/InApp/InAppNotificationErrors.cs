using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Domain.InApp;

/// <summary>The in-app notification errors. Each code has a message in <c>Resources/NotificationsErrorMessages.resx</c> and its translations.</summary>
internal static class InAppNotificationErrors
{
    /// <summary>Also the answer for another user's notification, so the response does not tell whether the id exists.</summary>
    public static Error NotFound(Guid id) =>
        Error.NotFound("notifications.notification_not_found", $"Notification '{id}' was not found.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };
}
