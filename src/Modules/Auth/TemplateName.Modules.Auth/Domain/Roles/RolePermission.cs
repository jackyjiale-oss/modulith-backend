namespace TemplateName.Modules.Auth.Domain.Roles;

/// <summary>The grant of a permission to a role.</summary>
internal sealed class RolePermission
{
    // EF Core materializes the entity through this constructor; the role creates grants.
    private RolePermission()
    {
    }

    public Guid RoleId { get; private set; }

    public Guid PermissionId { get; private set; }

    internal static RolePermission Create(Guid roleId, Guid permissionId) => new()
    {
        RoleId = roleId,
        PermissionId = permissionId,
    };
}
