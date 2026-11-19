using TemplateName.Application.Common.Concurrency;

namespace TemplateName.Modules.Auth.Application.Me;

/// <summary>
/// The version of the caller's profile (ADR 0038): display name, locale and time zone, which is what <c>PUT</c> me writes. The roles and
/// permissions <c>GET</c> me also returns are not covered: <c>PUT</c> me does not write them.
/// </summary>
internal static class ProfileVersion
{
    public static EntityTag Of(string displayName, string locale, string timeZone)
        => new EntityTagBuilder("profile").Add(displayName).Add(locale).Add(timeZone).Build();
}
