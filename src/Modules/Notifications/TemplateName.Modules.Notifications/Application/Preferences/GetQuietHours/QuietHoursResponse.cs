using System.Text.Json.Serialization;

namespace TemplateName.Modules.Notifications.Application.Preferences.GetQuietHours;

/// <summary>The caller's quiet hours: a daily window in the caller's local time during which non-critical email is held back.</summary>
/// <param name="Start">The first local time inside the window, as <c>"HH:mm"</c>; null when there is no window.</param>
/// <param name="End">The first local time after the window, as <c>"HH:mm"</c>; null when there is no window. Before <paramref name="Start"/> means the window crosses midnight.</param>
/// <param name="TimeZone">
/// The IANA time zone the times are read in: the one saved in the caller's Auth profile (read-only here; change it with
/// <c>PUT /api/v1/auth/me</c>), or <c>UTC</c> when that is empty or unknown, as the scheduler treats it.
/// </param>
internal sealed record QuietHoursResponse(
    [property: JsonConverter(typeof(HourMinuteJsonConverter))] TimeOnly? Start,
    [property: JsonConverter(typeof(HourMinuteJsonConverter))] TimeOnly? End,
    string TimeZone);
