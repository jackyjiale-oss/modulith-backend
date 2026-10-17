using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Preferences;

/// <summary>
/// The failure of a command that needs the caller's user id when the token carries none. The endpoint group already answers such a
/// request with the body-less 401, so this is a safety net; it has the same status and the common <c>http.401</c> code, which the
/// common messages translate.
/// </summary>
internal static class UnauthenticatedCaller
{
    public static readonly Error Error = Error.Unauthorized("http.401", "Authentication is required.");
}
