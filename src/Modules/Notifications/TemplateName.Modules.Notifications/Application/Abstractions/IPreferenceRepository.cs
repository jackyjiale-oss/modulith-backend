using TemplateName.Modules.Notifications.Domain.Preferences;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

internal interface IPreferenceRepository
{
    /// <summary>The user's stored preferences (tracked); a type and channel without a row use the type's default.</summary>
    Task<IReadOnlyList<UserPreference>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The user's profile (quiet hours), tracked; <see langword="null"/> when the user has none yet.</summary>
    Task<UserNotificationProfile?> GetSettingsAsync(Guid userId, CancellationToken cancellationToken);

    void Add(UserPreference preference);

    void Add(UserNotificationProfile profile);
}
