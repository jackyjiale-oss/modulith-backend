namespace TemplateName.Application.Common.Identity;

/// <summary>
/// Declares the permissions of one module. Any module registers one implementation as a singleton <see cref="IPermissionSource"/>;
/// the Auth module collects every registered source and syncs the definitions into its permission table.
/// </summary>
public interface IPermissionSource
{
    IReadOnlyCollection<PermissionDefinition> Permissions { get; }
}
