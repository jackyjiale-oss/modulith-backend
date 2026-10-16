using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Notifications.Application.Preferences.Get;

/// <summary>Reads the caller's notification preferences: every notification type with its channels and their state.</summary>
internal sealed record GetPreferencesQuery : IQuery<PreferencesResponse>;
