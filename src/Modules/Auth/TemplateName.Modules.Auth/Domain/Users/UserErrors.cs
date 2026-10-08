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

    public static Error NotFound(Guid id) =>
        Error.NotFound("auth.user_not_found", $"User '{id}' was not found.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };
}
