using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Preferences.Get;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;

namespace TemplateName.Modules.Notifications.Application.Preferences;

/// <summary>
/// The preferences as <c>GET</c> preferences shows them: every notification type by code, each default channel enabled unless the user
/// switched it off (a stored row) and it is not mandatory. The push channel is shown only while push is enabled
/// (<c>Notifications:Push:Enabled</c>). The read and the update both build it, so their versions agree (ADR 0038).
/// </summary>
internal static class PreferencesView
{
    /// <summary>Whether the user is offered <paramref name="channel"/> of <paramref name="type"/>: a default channel, and push only while enabled.</summary>
    public static bool Offers(NotificationTypeDefinition type, NotificationChannel channel, bool pushEnabled)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.DefaultChannels.Contains(channel) && (channel != NotificationChannel.Push || pushEnabled);
    }

    public static PreferencesResponse Build(NotificationCatalog catalog, IEnumerable<UserPreference> stored, bool pushEnabled)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(stored);
        var switchedOff = stored
            .Where(preference => !preference.IsEnabled)
            .Select(preference => (preference.TypeCode, preference.Channel))
            .ToHashSet();

        var types = catalog.All
            .OrderBy(type => type.Code, StringComparer.Ordinal)
            .Select(type => new TypePreferenceResponse(
                type.Code,
                type.Category,
                [.. type.DefaultChannels.Where(channel => Offers(type, channel, pushEnabled)).Order().Select(channel => new ChannelPreferenceResponse(
                    channel,
                    Enabled: type.IsMandatory(channel) || !switchedOff.Contains((type.Code, channel)),
                    Mandatory: type.IsMandatory(channel)))]))
            .ToList();

        return new PreferencesResponse(types);
    }
}
