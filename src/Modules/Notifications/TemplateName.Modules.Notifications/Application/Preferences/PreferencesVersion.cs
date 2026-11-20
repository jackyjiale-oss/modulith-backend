using TemplateName.Application.Common.Concurrency;
using TemplateName.Modules.Notifications.Application.Preferences.Get;

namespace TemplateName.Modules.Notifications.Application.Preferences;

/// <summary>The version of the preferences (ADR 0038): every (type, channel, enabled) of the view <c>GET</c> preferences returns.</summary>
internal static class PreferencesVersion
{
    public static EntityTag Of(PreferencesResponse view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new EntityTagBuilder("preferences")
            .AddSet(view.Types.SelectMany(type => type.Channels.Select(channel => $"{type.TypeCode}|{channel.Channel}|{channel.Enabled}")))
            .Build();
    }
}
