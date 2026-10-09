namespace TemplateName.Modules.Auth.Application.Me.GetMe;

/// <summary>The signed-in user. Never a password hash, a security stamp or any token.</summary>
/// <param name="Id">The user id, the access token's <c>sub</c>.</param>
/// <param name="Email">The address as the user typed it at registration (trimmed).</param>
/// <param name="EmailConfirmed">Whether the address has been confirmed.</param>
/// <param name="DisplayName">The name to show.</param>
/// <param name="Locale">The saved language, for example <c>en</c>, <c>ms</c> or <c>zh-Hans</c>.</param>
/// <param name="TimeZone">The saved time zone (IANA id), <c>UTC</c> until the user changes it.</param>
/// <param name="Roles">The names of the user's roles that are not deleted, sorted ordinally.</param>
/// <param name="Permissions">The permission codes those roles grant, sorted ordinally: the set every permission check uses.</param>
internal sealed record MeResponse(
    Guid Id,
    string Email,
    bool EmailConfirmed,
    string DisplayName,
    string Locale,
    string TimeZone,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions);
