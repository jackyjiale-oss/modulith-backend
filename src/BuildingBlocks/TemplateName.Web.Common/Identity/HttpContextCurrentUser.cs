using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TemplateName.Application.Common.Identity;

namespace TemplateName.Web.Common.Identity;

/// <summary>Reads the current user from the request principal: the <c>sub</c> claim, else <see cref="ClaimTypes.NameIdentifier"/>.</summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            var value = user?.FindFirstValue("sub") ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var userId) ? userId : null;
        }
    }

    public bool IsAuthenticated => httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;
}
