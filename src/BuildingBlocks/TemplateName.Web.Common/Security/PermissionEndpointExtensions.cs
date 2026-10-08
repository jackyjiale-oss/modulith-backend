using Microsoft.AspNetCore.Builder;

namespace TemplateName.Web.Common.Security;

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Requires an authenticated user who holds <paramref name="permission"/> (<c>module.resource.action</c>, compared exactly). The
    /// endpoint gets its own policy with one <c>PermissionRequirement</c>; there is no dynamic policy provider (decision D6, ADR 0016).
    /// An anonymous caller gets 401 and a signed-in caller without the permission 403. Requires <c>AddPermissionAuthorization</c>.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        return builder.RequireAuthorization(policy => policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission)));
    }
}
