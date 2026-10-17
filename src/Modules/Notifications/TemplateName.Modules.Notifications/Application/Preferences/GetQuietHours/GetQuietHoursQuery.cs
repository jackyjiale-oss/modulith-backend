using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Notifications.Application.Preferences.GetQuietHours;

/// <summary>Reads the caller's quiet hours and the time zone they are read in.</summary>
internal sealed record GetQuietHoursQuery : IQuery<QuietHoursResponse>;
