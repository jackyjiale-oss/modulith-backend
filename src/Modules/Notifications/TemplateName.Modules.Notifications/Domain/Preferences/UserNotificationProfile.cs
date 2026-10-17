using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Domain.Preferences;

/// <summary>A user's notification profile: the settings that are not per notification type, for now the quiet hours. Keyed by the user id.</summary>
internal sealed class UserNotificationProfile : Entity<Guid>
{
    // EF Core materializes the entity through this constructor; callers use Create.
    private UserNotificationProfile()
    {
    }

    /// <summary>The user these settings belong to; it is the key.</summary>
    public Guid UserId => Id;

    /// <summary>The daily window, in the user's time zone, during which non-critical email is held back; <see langword="null"/> for none.</summary>
    public QuietHours? QuietHours { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The optimistic-concurrency token; the database changes it on every update.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public static UserNotificationProfile Create(Guid userId, DateTimeOffset now) =>
        new()
        {
            Id = userId,
            UpdatedAt = now,
        };

    /// <summary>Sets the quiet hours, or clears them with <see langword="null"/>.</summary>
    public void SetQuietHours(QuietHours? quietHours, DateTimeOffset now)
    {
        QuietHours = quietHours;
        UpdatedAt = now;
    }
}
