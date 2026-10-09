using System.Reflection;

namespace TemplateName.Modules.Auth.Domain.Audit;

/// <summary>The event types written to the auth audit log. The values are stored, so they are never renamed.</summary>
internal static class AuthAuditEvents
{
    public const string Registered = "auth.registered";
    public const string RegisterDuplicate = "auth.register_duplicate";
    public const string EmailConfirmed = "auth.email_confirmed";
    public const string EmailConfirmFailed = "auth.email_confirm_failed";
    public const string ConfirmationResent = "auth.confirmation_resent";
    public const string LoginSucceeded = "auth.login_succeeded";
    public const string LoginFailed = "auth.login_failed";
    public const string LockedOut = "auth.locked_out";
    public const string TokenRefreshed = "auth.token_refreshed";
    public const string RefreshFailed = "auth.refresh_failed";
    public const string TokenReuseDetected = "auth.token_reuse_detected";
    public const string Logout = "auth.logout";
    public const string LogoutAll = "auth.logout_all";
    public const string SessionRevoked = "auth.session_revoked";
    public const string PasswordForgotRequested = "auth.password_forgot_requested";
    public const string PasswordReset = "auth.password_reset";
    public const string PasswordResetFailed = "auth.password_reset_failed";
    public const string PasswordChanged = "auth.password_changed";
    public const string PasswordChangeFailed = "auth.password_change_failed";
    public const string ProfileUpdated = "auth.profile_updated";
    public const string AdminUserCreated = "auth.admin_user_created";
    public const string AdminUserLocked = "auth.admin_user_locked";
    public const string AdminUserUnlocked = "auth.admin_user_unlocked";
    public const string AdminPasswordResetForced = "auth.admin_password_reset_forced";
    public const string AdminSessionsRevoked = "auth.admin_sessions_revoked";
    public const string AdminRolesAssigned = "auth.admin_roles_assigned";
    public const string RoleCreated = "auth.role_created";
    public const string RoleUpdated = "auth.role_updated";
    public const string RoleDeleted = "auth.role_deleted";
    public const string RolePermissionsChanged = "auth.role_permissions_changed";

    /// <summary>Every event type above, for the audit log's <c>eventType</c> filter.</summary>
    public static IReadOnlyCollection<string> All { get; } =
    [
        .. typeof(AuthAuditEvents)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!),
    ];
}
