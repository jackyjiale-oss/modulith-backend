namespace TemplateName.Modules.Notifications.Domain.Notifications;

/// <summary>Where a notification is delivered. The values are persisted: never renumber or reuse them.</summary>
internal enum NotificationChannel : byte
{
    Email = 1,
    InApp = 2,

    /// <summary>A push to one of the recipient's devices (FCM); one delivery per device.</summary>
    Push = 3,

    /// <summary>A text message to one phone number (Twilio); at most one delivery per notification.</summary>
    Sms = 4,
}
