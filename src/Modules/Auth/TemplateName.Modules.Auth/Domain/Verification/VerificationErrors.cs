using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Verification;

/// <summary>The verification errors. Each code has a message in <c>Resources/AuthErrorMessages.resx</c> and its translations.</summary>
internal static class VerificationErrors
{
    /// <summary>One answer for a token that is unknown, used, replaced, expired or meant for another purpose, so the response does not tell which.</summary>
    public static readonly Error InvalidToken = Error.Validation(
        "auth.invalid_token",
        "The link is invalid or has expired.");
}
