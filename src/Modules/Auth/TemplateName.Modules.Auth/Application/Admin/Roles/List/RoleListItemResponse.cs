namespace TemplateName.Modules.Auth.Application.Admin.Roles.List;

/// <summary>A role as the list shows it.</summary>
/// <param name="Id">The role id.</param>
/// <param name="Name">The role's name.</param>
/// <param name="Description">What the role is for; may be empty.</param>
/// <param name="IsSystem">Whether it is a seeded system role.</param>
/// <param name="PermissionCount">How many permissions the role holds, grants of withdrawn (deprecated) permissions included.</param>
/// <param name="CreatedAt">When the role was created (UTC).</param>
internal sealed record RoleListItemResponse(
    Guid Id,
    string Name,
    string Description,
    bool IsSystem,
    int PermissionCount,
    DateTime CreatedAt);
