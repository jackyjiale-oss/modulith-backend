namespace TemplateName.Modules.Auth.Domain.Roles;

/// <summary>The names of the roles the module seeds. They cannot be renamed or deleted.</summary>
internal static class SystemRoles
{
    /// <summary>Holds every permission; its permission set is maintained by the seeder, never by hand.</summary>
    public const string SuperAdmin = "SuperAdmin";

    public const string Admin = "Admin";

    public const string User = "User";
}
