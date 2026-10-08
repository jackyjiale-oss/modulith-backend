namespace TemplateName.Modules.Auth.Application;

/// <summary>The permission codes of the Auth module, <c>module.resource.action</c> in snake case. Endpoints require them by constant.</summary>
internal static class AuthPermissions
{
    public const string UserView = "auth.user.view";

    public const string UserCreate = "auth.user.create";

    public const string UserLock = "auth.user.lock";

    public const string UserResetPassword = "auth.user.reset_password";

    public const string UserRevokeSessions = "auth.user.revoke_sessions";

    public const string UserAssignRoles = "auth.user.assign_roles";

    public const string RoleView = "auth.role.view";

    public const string RoleManage = "auth.role.manage";

    public const string PermissionView = "auth.permission.view";

    public const string AuditView = "auth.audit.view";
}
