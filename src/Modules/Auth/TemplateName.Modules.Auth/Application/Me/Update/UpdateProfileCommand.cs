using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Me.GetMe;

namespace TemplateName.Modules.Auth.Application.Me.Update;

/// <summary>Changes the signed-in user's display name, language and time zone.</summary>
/// <param name="DisplayName">The name to show.</param>
/// <param name="Locale">A predefined culture name, such as <c>en</c>, <c>ms</c> or <c>zh-Hans</c>.</param>
/// <param name="TimeZone">An IANA time zone id, such as <c>Asia/Kuala_Lumpur</c> or <c>UTC</c>.</param>
internal sealed record UpdateProfileCommand(string DisplayName, string Locale, string TimeZone) : ICommand<MeResponse>;
