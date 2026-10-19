using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Preferences.Get;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Preferences.Update;

/// <summary>
/// Changes the caller's preferences for the items named; every other preference stays as it was. One to 100 items; the request is all
/// or nothing. Two items for the same type and channel are applied in order, so the last one wins.
/// </summary>
/// <param name="Items">The choices to apply.</param>
internal sealed record UpdatePreferencesCommand(IReadOnlyList<ChannelPreferenceUpdate> Items) : ICommand<PreferencesResponse>;

/// <summary>The caller's choice for one channel of one notification type.</summary>
/// <param name="TypeCode">The notification type.</param>
/// <param name="Channel">The channel; one of the type's own.</param>
/// <param name="Enabled">True to receive the type through the channel; false to switch it off (refused for a mandatory channel).</param>
internal sealed record ChannelPreferenceUpdate(string TypeCode, NotificationChannel Channel, bool Enabled);
