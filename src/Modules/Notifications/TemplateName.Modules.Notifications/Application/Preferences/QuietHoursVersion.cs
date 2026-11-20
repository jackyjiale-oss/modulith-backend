using TemplateName.Application.Common.Concurrency;

namespace TemplateName.Modules.Notifications.Application.Preferences;

/// <summary>
/// The version of the quiet hours (ADR 0038): the window start and end, both null without a window or without a settings row. The time
/// zone <c>GET</c> quiet hours also returns belongs to Auth and is not covered.
/// </summary>
internal static class QuietHoursVersion
{
    public static EntityTag Of(TimeOnly? start, TimeOnly? end) => new EntityTagBuilder("quiet-hours").Add(start).Add(end).Build();
}
