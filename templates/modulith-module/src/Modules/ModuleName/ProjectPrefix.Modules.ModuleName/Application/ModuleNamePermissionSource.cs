using ProjectPrefix.Application.Common.Identity;

namespace ProjectPrefix.Modules.ModuleName.Application;

/// <summary>
/// Declares the permissions of the ModuleName module. The Auth module's seeder syncs every registered permission source into its
/// permission table; this module never references the Auth module.
/// </summary>
internal sealed class ModuleNamePermissionSource : IPermissionSource
{
    // Add the first permission here: a PermissionDefinition(code, module, name, description) with its code constant in ModuleNamePermissions.
    private static readonly PermissionDefinition[] All = [];

    public IReadOnlyCollection<PermissionDefinition> Permissions => All;
}
