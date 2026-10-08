using TemplateName.Application.Common.Identity;

namespace TemplateName.Modules.Auth.Application;

/// <summary>Declares the permissions of the Auth module so the seeder can sync them into the permission table.</summary>
internal sealed class AuthPermissionSource : IPermissionSource
{
    private const string ModuleName = "auth";

    private static readonly PermissionDefinition[] All =
    [
        Define(AuthPermissions.UserView, "View users", "List users and read their details."),
        Define(AuthPermissions.UserCreate, "Create users", "Create a user account on behalf of someone."),
        Define(AuthPermissions.UserLock, "Lock users", "Suspend and reinstate user accounts."),
        Define(AuthPermissions.UserResetPassword, "Reset passwords", "Send a password reset to a user."),
        Define(AuthPermissions.UserRevokeSessions, "Revoke sessions", "End the sessions of a user."),
        Define(AuthPermissions.UserAssignRoles, "Assign roles", "Add and remove the roles of a user."),
        Define(AuthPermissions.RoleView, "View roles", "List roles and read their permissions."),
        Define(AuthPermissions.RoleManage, "Manage roles", "Create, change and delete roles and set their permissions."),
        Define(AuthPermissions.PermissionView, "View permissions", "List the permissions that modules declare."),
        Define(AuthPermissions.AuditView, "View the audit log", "Read the authentication and administration audit log."),
    ];

    public IReadOnlyCollection<PermissionDefinition> Permissions => All;

    private static PermissionDefinition Define(string code, string name, string description) =>
        new(code, ModuleName, name, description);
}
