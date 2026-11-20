using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Preferences.Get;
using TemplateName.Modules.Notifications.Domain.Preferences;

namespace TemplateName.Modules.Notifications.Application.Preferences;

/// <summary>
/// The preferences as <c>GET</c> preferences shows them: every notification type by code, each default channel enabled unless the user
/// switched it off (a stored row) and it is not mandatory. The read and the update both build it, so their versions agree (ADR 0038).
/// </summary>
internal static class PreferencesView
{
    public static PreferencesResponse Build(NotificationCatalog catalog, IEnumerable<UserPreference> stored)
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
                [.. type.DefaultChannels.Order().Select(channel => new ChannelPreferenceResponse(
                    channel,
                    Enabled: type.IsMandatory(channel) || !switchedOff.Contains((type.Code, channel)),
                    Mandatory: type.IsMandatory(channel)))]))
            .ToList();

        return new PreferencesResponse(types);
    }
}
