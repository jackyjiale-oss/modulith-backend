using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using TemplateName.Application.Common.Identity;

namespace TemplateName.Web.Common.Security;

/// <summary>
/// Meets a <see cref="PermissionRequirement"/> only for an authenticated user with a user id whose permissions, resolved on the server
/// by <see cref="IPermissionChecker"/>, include the code (ADR 0016). It fails closed: an anonymous caller, a principal without a user
/// id and a denied check leave the requirement unmet, and a checker exception propagates instead of granting.
/// </summary>
internal sealed class PermissionAuthorizationHandler(ICurrentUser currentUser, IPermissionChecker permissionChecker)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext httpContext ? httpContext.RequestAborted : CancellationToken.None;
        if (await permissionChecker.HasPermissionAsync(userId, requirement.Permission, cancellationToken))
        {
            context.Succeed(requirement);
        }
    }
}
