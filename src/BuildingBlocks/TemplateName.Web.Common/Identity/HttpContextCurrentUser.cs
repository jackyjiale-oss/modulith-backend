using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TemplateName.Application.Common.Identity;

namespace TemplateName.Web.Common.Identity;

/// <summary>
/// Reads the current user from the request principal: the <c>sub</c> claim, else <see cref="ClaimTypes.NameIdentifier"/>, and the
/// session from the <c>sid</c> claim. A value that is not a <see cref="Guid"/> reads as null. <see cref="AuthTime"/> is the <c>auth_time</c>
/// claim as Unix seconds and <see cref="AuthMethods"/> lists the <c>amr</c> claims.
/// </summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private const string SessionIdClaimType = "sid";
    private const string AuthTimeClaimType = "auth_time";
    private const string AuthMethodClaimType = "amr";

    public Guid? UserId
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            var value = user?.FindFirstValue("sub") ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var userId) ? userId : null;
        }
    }

    public Guid? SessionId
        => Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirstValue(SessionIdClaimType), out var sessionId) ? sessionId : null;

    public DateTimeOffset? AuthTime
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.User.FindFirstValue(AuthTimeClaimType);
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && IsUnixSeconds(seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
    }

    public IReadOnlyCollection<string> AuthMethods
        => httpContextAccessor.HttpContext?.User.FindAll(AuthMethodClaimType).Select(claim => claim.Value).ToArray() ?? [];

    public bool IsAuthenticated => httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    private static bool IsUnixSeconds(long seconds) => seconds is >= -62135596800 and <= 253402300799;
}
