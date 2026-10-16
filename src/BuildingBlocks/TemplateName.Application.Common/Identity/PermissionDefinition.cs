namespace TemplateName.Application.Common.Identity;

/// <summary>
/// A permission a module declares in code. <paramref name="Code"/> is <c>module.resource.action</c> in snake case (for example
/// <c>auth.user.view</c>); the Auth module syncs every declared permission into its table at startup.
/// </summary>
/// <param name="Code">The unique permission code.</param>
/// <param name="Module">The module that owns the permission (the first segment of the code).</param>
/// <param name="Name">A short English name for administration screens.</param>
/// <param name="Description">What the permission allows, in English.</param>
/// <param name="AdminDefault">
/// Whether the <c>Admin</c> system role gets the permission when it is first declared: the Auth seeder grants it in the run that inserts
/// the code, and never again (an administrator may have removed it; <c>SuperAdmin</c> always holds every permission regardless).
/// </param>
public sealed record PermissionDefinition(string Code, string Module, string Name, string Description, bool AdminDefault = false);
