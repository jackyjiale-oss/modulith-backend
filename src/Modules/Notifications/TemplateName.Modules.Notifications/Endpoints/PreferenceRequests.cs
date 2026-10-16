using System.Text.Json.Serialization;
using TemplateName.Modules.Notifications.Application.Preferences.GetQuietHours;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Endpoints;

/// <summary>The body of <c>PUT /api/v1/notifications/preferences</c>.</summary>
/// <param name="Items">One to 100 choices; only the items named are changed.</param>
internal sealed record UpdatePreferencesRequest(IReadOnlyList<ChannelPreferenceRequest?>? Items);

/// <summary>
/// One choice in <see cref="UpdatePreferencesRequest"/>. All three members are required: a missing <c>enabled</c> must not silently
/// mean "switch it off", so a body without one is 400 <c>request.malformed</c>.
/// </summary>
/// <param name="TypeCode">The notification type, for example <c>auth.password_changed</c>.</param>
/// <param name="Channel">The channel, by name: <c>Email</c> or <c>InApp</c>.</param>
/// <param name="Enabled">True to receive the type through the channel; false to switch it off.</param>
internal sealed record ChannelPreferenceRequest(
    [property: JsonRequired] string TypeCode,
    [property: JsonRequired] NotificationChannel Channel,
    [property: JsonRequired] bool Enabled);

/// <summary>
/// The body of <c>PUT /api/v1/notifications/preferences/quiet-hours</c>: the new window, or null for both to clear it. A missing member
/// counts as null. Times are 24-hour <c>"HH:mm"</c> strings, such as <c>"22:00"</c>.
/// </summary>
/// <param name="Start">The first local time inside the window.</param>
/// <param name="End">The first local time after the window; before <paramref name="Start"/> means the window crosses midnight.</param>
internal sealed record UpdateQuietHoursRequest(
    [property: JsonConverter(typeof(HourMinuteJsonConverter))] TimeOnly? Start,
    [property: JsonConverter(typeof(HourMinuteJsonConverter))] TimeOnly? End);
