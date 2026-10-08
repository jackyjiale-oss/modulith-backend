using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Roles;

/// <summary>The role and permission errors. Each code has a message in <c>Resources/AuthErrorMessages.resx</c> and its translations.</summary>
internal static class RoleErrors
{
    public static readonly Error SystemRoleProtected = Error.Conflict(
        "auth.system_role_protected",
        "This is a system role and cannot be changed this way.");

    public static readonly Error PermissionNotFound = Error.Validation(
        "auth.permission_not_found",
        "One or more of the permissions do not exist.");

    public static Error NotFound(Guid id) =>
        Error.NotFound("auth.role_not_found", $"Role '{id}' was not found.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };

    public static Error NameTaken(string name) =>
        Error.Conflict("auth.role_name_taken", $"A role named '{name}' already exists.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["name"] = name },
        };
}
