using TemplateName.Application.Common.Concurrency;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Application.Admin.Roles;

/// <summary>
/// The version of a role's editable state (ADR 0038): its name, description and granted permission ids, which is what
/// <c>PUT</c> role and <c>PUT</c> role permissions write; both updates and <c>GET</c> role use it, so they share one version.
/// </summary>
internal static class RoleVersion
{
    public static EntityTag Of(string name, string description, IEnumerable<Guid> permissionIds)
        => new EntityTagBuilder("role").Add(name).Add(description).AddSet(permissionIds).Build();

    /// <summary>The version of a loaded role, as its current grants stand.</summary>
    public static EntityTag Of(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return Of(role.Name, role.Description, role.Permissions.Select(grant => grant.PermissionId));
    }
}
