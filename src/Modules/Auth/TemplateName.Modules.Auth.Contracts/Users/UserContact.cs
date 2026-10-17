namespace TemplateName.Modules.Auth.Contracts.Users;

/// <summary>
/// How to reach a user and in which language and time zone: what a module that notifies users may know about them, nothing more (no
/// status, roles, hashes or tokens).
/// </summary>
/// <param name="UserId">The user id.</param>
/// <param name="Email">The user's current email address, as the user wrote it (not normalized).</param>
/// <param name="DisplayName">The name to greet the user with.</param>
/// <param name="Locale">
/// The user's saved language as stored (for example <c>ms</c> or <c>zh-CN</c>); never null, but empty when none was saved. The caller
/// resolves it to a language it supports and falls back to its own default.
/// </param>
/// <param name="TimeZone">
/// The user's saved IANA time zone id as stored (<c>UTC</c> by default); never null. The caller treats an unknown or empty id as <c>UTC</c>.
/// </param>
public sealed record UserContact(Guid UserId, string Email, string DisplayName, string Locale, string TimeZone);
