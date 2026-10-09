namespace TemplateName.Application.Common.Identity;

/// <summary>
/// A permission a module declares in code. <paramref name="Code"/> is <c>module.resource.action</c> in snake case (for example
/// <c>auth.user.view</c>); the Auth module syncs every declared permission into its table at startup.
/// </summary>
/// <param name="Code">The unique permission code.</param>
/// <param name="Module">The module that owns the permission (the first segment of the code).</param>
/// <param name="Name">A short English name for administration screens.</param>
/// <param name="Description">What the permission allows, in English.</param>
public sealed record PermissionDefinition(string Code, string Module, string Name, string Description);
