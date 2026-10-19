namespace TemplateName.Modules.Auth.Domain.Verification;

/// <summary>
/// Who caused a verification code to be issued, so the message that carries its link can say why it was sent. Only a
/// <see cref="VerificationPurpose.PasswordReset"/> code has an administrator trigger. It travels in the issued event's outbox row by its
/// numeric value, so a new trigger is appended and existing values are never renumbered; <see cref="SelfService"/> is 0, the value a row
/// written before the trigger existed reads as.
/// </summary>
internal enum VerificationTrigger
{
    /// <summary>The user asked: registration, a resend or forgot-password.</summary>
    SelfService = 0,

    /// <summary>An administrator created the account; the link sets its first password.</summary>
    CreatedByAdmin = 1,

    /// <summary>An administrator forced a password reset.</summary>
    ForcedByAdmin = 2,
}
