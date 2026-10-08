namespace TemplateName.Modules.Auth.Application.Admin.Permissions.List;

/// <summary>A permission as the list shows it.</summary>
/// <param name="Id">The permission id, as <c>PUT .../roles/{id}/permissions</c> takes it.</param>
/// <param name="Code">The code, <c>module.resource.action</c>.</param>
/// <param name="Module">The module that declares it, the first segment of the code.</param>
/// <param name="Name">The English name.</param>
/// <param name="Description">What it allows.</param>
/// <param name="IsDeprecated">Whether no module declares it any more; such a permission allows nothing and cannot be newly granted.</param>
internal sealed record PermissionResponse(Guid Id, string Code, string Module, string Name, string Description, bool IsDeprecated);
