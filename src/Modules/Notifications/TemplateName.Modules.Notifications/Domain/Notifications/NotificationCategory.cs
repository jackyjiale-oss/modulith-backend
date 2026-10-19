namespace TemplateName.Modules.Notifications.Domain.Notifications;

/// <summary>What a notification is about. The values are persisted: never renumber or reuse them.</summary>
internal enum NotificationCategory : byte
{
    Security = 1,
    Account = 2,
    System = 3,
}
