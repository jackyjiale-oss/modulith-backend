using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Domain.Preferences;

/// <summary>
/// A user's choice to receive (or not) one notification type on one channel. A row exists only when the choice differs from the type's
/// default (D10). The key is <c>(UserId, TypeCode, Channel)</c>.
/// </summary>
internal sealed class UserPreference
{
    // EF Core materializes the entity through this constructor; callers use Create.
    private UserPreference()
    {
    }

    public Guid UserId { get; private set; }

    public string TypeCode { get; private set; } = string.Empty;

    public NotificationChannel Channel { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static UserPreference Create(Guid userId, string typeCode, NotificationChannel channel, bool isEnabled, DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            TypeCode = typeCode,
            Channel = channel,
            IsEnabled = isEnabled,
            UpdatedAt = now,
        };

    public void SetEnabled(bool isEnabled, DateTimeOffset now)
    {
        IsEnabled = isEnabled;
        UpdatedAt = now;
    }
}
