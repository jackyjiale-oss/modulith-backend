using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Preferences.Get;

/// <summary>
/// The caller's preferences for every notification type the application declares, ordered by type code (ordinal). The set is fixed in
/// code, not a growing list, so it is not paged.
/// </summary>
/// <param name="Types">One entry per type.</param>
internal sealed record PreferencesResponse(IReadOnlyList<TypePreferenceResponse> Types);

/// <summary>One notification type and the state of each of its channels.</summary>
/// <param name="TypeCode">The type code, for example <c>auth.password_changed</c>.</param>
/// <param name="Category">What the type is about.</param>
/// <param name="Channels">The type's channels, in channel order (<c>Email</c>, then <c>InApp</c>); only channels the type is delivered through.</param>
internal sealed record TypePreferenceResponse(string TypeCode, NotificationCategory Category, IReadOnlyList<ChannelPreferenceResponse> Channels);

/// <summary>One channel of one type.</summary>
/// <param name="Channel">The channel.</param>
/// <param name="Enabled">Whether the caller receives the type through the channel: true unless a stored choice switched it off; always true for a mandatory channel.</param>
/// <param name="Mandatory">True when the channel cannot be switched off for this type (a security notice's email).</param>
internal sealed record ChannelPreferenceResponse(NotificationChannel Channel, bool Enabled, bool Mandatory);
