using Microsoft.AspNetCore.Authorization;

namespace TemplateName.Web.Common.Security;

/// <summary>Requires the signed-in user to hold <paramref name="Permission"/>. Added to an endpoint by <c>RequirePermission</c>.</summary>
/// <param name="Permission">The permission code, <c>module.resource.action</c>.</param>
internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;
