using TemplateName.Application.Common.Concurrency;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Preferences.GetQuietHours;

namespace TemplateName.Modules.Notifications.Application.Preferences.UpdateQuietHours;

/// <summary>
/// Replaces the caller's quiet hours. Both null clears them; one null, or equal values, is
/// <c>notifications.invalid_quiet_hours</c>.
/// </summary>
/// <param name="Start">The first local time inside the window.</param>
/// <param name="End">The first local time after the window.</param>
internal sealed record UpdateQuietHoursCommand(TimeOnly? Start, TimeOnly? End) : ICommand<QuietHoursResponse>
{
    /// <summary>The <c>If-Match</c> precondition of the caller; none when the header is absent.</summary>
    public IfMatch IfMatch { get; init; } = IfMatch.None;
}
