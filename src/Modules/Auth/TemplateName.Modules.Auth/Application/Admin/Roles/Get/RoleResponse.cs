namespace TemplateName.Modules.Auth.Application.Admin.Roles.Get;

/// <summary>A role with the permissions it grants, in code order.</summary>
/// <param name="Id">The role id.</param>
/// <param name="Name">The role's name.</param>
/// <param name="Description">What the role is for; may be empty.</param>
/// <param name="IsSystem">Whether it is a seeded system role, which cannot be renamed or deleted.</param>
/// <param name="CreatedAt">When the role was created (UTC).</param>
/// <param name="Permissions">The granted permissions, including ones that modules no longer declare (see <see cref="RolePermissionResponse.IsDeprecated"/>).</param>
internal sealed record RoleResponse(
    Guid Id,
    string Name,
    string Description,
    bool IsSystem,
    DateTime CreatedAt,
    IReadOnlyList<RolePermissionResponse> Permissions);

/// <summary>A permission granted to a role.</summary>
/// <param name="Id">The permission id, as <c>PUT .../permissions</c> takes it.</param>
/// <param name="Code">The permission code.</param>
/// <param name="IsDeprecated">Whether no module declares the permission any more: the grant stays but allows nothing.</param>
internal sealed record RolePermissionResponse(Guid Id, string Code, bool IsDeprecated);
