using TemplateName.Application.Common.Identity;

namespace TemplateName.Modules.Auth.Application;

/// <summary>Declares the permissions of the Auth module so the seeder can sync them into the permission table.</summary>
internal sealed class AuthPermissionSource : IPermissionSource
{
    private const string ModuleName = "auth";

    // AdminDefault: the permissions the seeded Admin role gets (see AuthSeeder): account administration without role assignment, read
    // access to roles, permissions and the audit log.
    private static readonly PermissionDefinition[] All =
    [
        Define(AuthPermissions.UserView, "View users", "List users and read their details.", adminDefault: true),
        Define(AuthPermissions.UserCreate, "Create users", "Create a user account on behalf of someone.", adminDefault: true),
        Define(AuthPermissions.UserLock, "Lock users", "Suspend and reinstate user accounts.", adminDefault: true),
        Define(AuthPermissions.UserResetPassword, "Reset passwords", "Send a password reset to a user.", adminDefault: true),
        Define(AuthPermissions.UserRevokeSessions, "Revoke sessions", "End the sessions of a user.", adminDefault: true),
        Define(AuthPermissions.UserResetMfa, "Reset two-step verification", "Turn off a user's two-step verification so they can set it up again.", adminDefault: true),
        Define(AuthPermissions.UserAssignRoles, "Assign roles", "Add and remove the roles of a user."),
        Define(AuthPermissions.RoleView, "View roles", "List roles and read their permissions.", adminDefault: true),
        Define(AuthPermissions.RoleManage, "Manage roles", "Create, change and delete roles and set their permissions."),
        Define(AuthPermissions.PermissionView, "View permissions", "List the permissions that modules declare.", adminDefault: true),
        Define(AuthPermissions.AuditView, "View the audit log", "Read the authentication and administration audit log.", adminDefault: true),
    ];

    public IReadOnlyCollection<PermissionDefinition> Permissions => All;

    private static PermissionDefinition Define(string code, string name, string description, bool adminDefault = false) =>
        new(code, ModuleName, name, description, adminDefault);
}
