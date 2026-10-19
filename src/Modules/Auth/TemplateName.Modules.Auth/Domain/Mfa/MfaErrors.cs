using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Mfa;

/// <summary>The two-step verification errors. Each code has a message in <c>Resources/AuthErrorMessages.resx</c> and its translations.</summary>
internal static class MfaErrors
{
    /// <summary>One answer for a challenge that is unknown, expired, used, exhausted, or whose account changed, so the response does not tell which.</summary>
    public static readonly Error InvalidChallenge = Error.Validation(
        "auth.invalid_mfa_challenge",
        "The sign-in attempt is invalid or has expired. Sign in again.");

    /// <summary>A wrong, reused or unreadable code, or a wrong recovery code. Deliberately a 400, not a 401, so a client asks for a new code instead of refreshing its token.</summary>
    public static readonly Error InvalidCode = Error.Validation(
        "auth.invalid_mfa_code",
        "The verification code is incorrect.");

    public static readonly Error AlreadyEnabled = Error.Conflict(
        "auth.mfa_already_enabled",
        "Two-step verification is already turned on.");

    public static readonly Error NotEnabled = Error.Conflict(
        "auth.mfa_not_enabled",
        "Two-step verification is not turned on.");

    public static readonly Error SetupNotPending = Error.Conflict(
        "auth.mfa_setup_not_pending",
        "There is no authenticator setup to confirm. Start the setup again.");

    public static readonly Error StepUpRequired = Error.Forbidden(
        "auth.step_up_required",
        "Confirm your identity again to continue.");

    public static readonly Error StepUpMethodNotAllowed = Error.Validation(
        "auth.step_up_method_not_allowed",
        "This way of confirming your identity is not available for your account.");

    /// <summary>The code of <see cref="MethodNotFound"/>, so an endpoint can tell it from the user-not-found error it answers as a 401.</summary>
    public const string MethodNotFoundCode = "auth.mfa_method_not_found";

    public static Error MethodNotFound(Guid id) =>
        Error.NotFound(MethodNotFoundCode, $"Verification method '{id}' was not found.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };
}
