using TemplateName.Application.Common.Concurrency;
using TemplateName.Modules.Notifications.Application.Preferences.Get;

namespace TemplateName.Modules.Notifications.Application.Preferences;

/// <summary>
/// The version of the preferences (ADR 0038): every (type, channel, enabled) of the view <c>GET</c> preferences returns, defaults
/// included, not only the stored rows. That view is what the client edits, so a release that adds a type or a channel, or changes a
/// default, changes the version, and a tag read before it answers 412 once.
/// </summary>
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
