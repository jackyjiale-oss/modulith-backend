namespace TemplateName.Modules.Notifications.Domain.Notifications;

/// <summary>How urgent a notification is; <see cref="Critical"/> ignores quiet hours. The values are persisted: never renumber or reuse them.</summary>
internal enum NotificationPriority : byte
{
    Low = 1,
    Normal = 2,
    High = 3,
    Critical = 4,
}
