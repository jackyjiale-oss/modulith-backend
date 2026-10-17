namespace TemplateName.Modules.Notifications.Application.Catalog;

/// <summary>
/// The codes of the notification types Auth's integration events turn into (Decision D6). A code is also the middle of the names of its
/// embedded templates (<c>Templates/Email/auth.password_reset.en.subject.scriban</c>), so renaming one renames its files.
/// </summary>
internal static class AuthNotificationTypes
{
    /// <summary>The link that confirms an email address (<c>EmailVerificationRequested</c>).</summary>
    public const string EmailVerification = "auth.email_verification";

    /// <summary>The link a user asked for to choose a new password (<c>PasswordResetRequested</c>, self service).</summary>
    public const string PasswordReset = "auth.password_reset";

    /// <summary>The link an administrator forced on a user to choose a new password (<c>PasswordResetRequested</c>, forced by admin).</summary>
    public const string PasswordResetRequired = "auth.password_reset_required";

    /// <summary>The link that lets a user whose account an administrator created set the first password (<c>PasswordResetRequested</c>, created by admin).</summary>
    public const string AccountCreated = "auth.account_created";

    /// <summary>Someone signed up with an address that already has an account (<c>RegistrationAttempted</c>); no link.</summary>
    public const string RegistrationAttempted = "auth.registration_attempted";

    /// <summary>The password was changed (<c>PasswordChanged</c>); email and an optional in-app message.</summary>
    public const string PasswordChanged = "auth.password_changed";

    /// <summary>Too many failed sign-ins locked the account for a while (<c>UserLockedOut</c>).</summary>
    public const string AccountLocked = "auth.account_locked";

    /// <summary>A refresh token was presented twice, so a session was signed out (<c>RefreshTokenReuseDetected</c>); email and an optional in-app message.</summary>
    public const string TokenReuseDetected = "auth.token_reuse_detected";
}
