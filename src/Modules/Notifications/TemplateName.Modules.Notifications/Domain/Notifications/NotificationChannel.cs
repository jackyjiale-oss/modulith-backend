namespace TemplateName.Modules.Notifications.Domain.Notifications;

/// <summary>Where a notification is delivered. The values are persisted: never renumber or reuse them.</summary>
internal enum NotificationChannel : byte
{
    Email = 1,
    InApp = 2,
}
