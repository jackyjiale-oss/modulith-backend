using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users;

/// <summary>The user errors. Each code has a message in <c>Resources/AuthErrorMessages.resx</c> and its translations.</summary>
internal static class UserErrors
{
    public static readonly Error InvalidCredentials = Error.Unauthorized(
        "auth.invalid_credentials",
        "The email or password is incorrect.");

    public static readonly Error EmailNotVerified = Error.Forbidden(
        "auth.email_not_verified",
        "The email address has not been verified.");

    public static readonly Error AccountInactive = Error.Forbidden(
        "auth.account_inactive",
        "The account is not active.");

    public static readonly Error PasswordReused = Error.Validation(
        "auth.password_reused",
        "The password was used recently. Choose a different one.");

    public static readonly Error PasswordBreached = Error.Validation(
        "auth.password_breached",
        "The password appears in a known data breach. Choose a different one.");

    public static readonly Error CurrentPasswordIncorrect = Error.Validation(
        "auth.current_password_incorrect",
        "The current password is incorrect.");

    /// <summary>An administrator created an account for an address that already has one (enumeration is no concern there).</summary>
    public static readonly Error EmailTaken = Error.Conflict(
        "auth.email_taken",
        "An account with this email address already exists.");

    public static readonly Error CannotLockSelf = Error.Validation(
        "auth.cannot_lock_self",
        "You cannot lock your own account.");

    /// <summary>The change would leave no active SuperAdmin, so nobody could administer SuperAdmin accounts any more.</summary>
    public static readonly Error LastSuperAdmin = Error.Conflict(
        "auth.last_super_admin",
        "The last active SuperAdmin cannot be locked or lose the SuperAdmin role.");

    /// <summary>Only a SuperAdmin may act on a SuperAdmin account or grant the SuperAdmin role.</summary>
    public static readonly Error CannotManageSuperAdmin = Error.Forbidden(
        "auth.cannot_manage_super_admin",
        "Only a SuperAdmin can manage a SuperAdmin account or grant the SuperAdmin role.");

    /// <summary>Roles were asked for at creation by a caller who may not assign roles.</summary>
    public static readonly Error RoleAssignmentNotAllowed = Error.Forbidden(
        "auth.role_assignment_not_allowed",
        "You may not assign roles, so a new account can only get the default role.");

    public static Error NotFound(Guid id) =>
        Error.NotFound("auth.user_not_found", $"User '{id}' was not found.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };
}
