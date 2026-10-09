namespace TemplateName.Modules.Auth.Domain.Verification;

/// <summary>What a verification code may be used for. Persisted by its numeric value, so a new purpose is appended and existing values are never renumbered.</summary>
internal enum VerificationPurpose
{
    EmailVerify = 1,
    PasswordReset = 2,
}
