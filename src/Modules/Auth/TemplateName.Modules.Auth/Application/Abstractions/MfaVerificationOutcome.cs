namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>How the second step of a sign-in ended, as counted by <see cref="IAuthMetrics.RecordMfaVerification"/>.</summary>
internal enum MfaVerificationOutcome
{
    /// <summary>The code was right and the challenge was consumed: tokens were issued.</summary>
    Succeeded = 0,

    /// <summary>A wrong, reused or unreadable code, or a wrong or used recovery code; counted on the challenge and the user.</summary>
    InvalidCode = 1,

    /// <summary>
    /// The challenge could not be used: unknown, expired, used, exhausted, its account changed, locked or gone, or it was lost to a
    /// simultaneous request. Nothing was counted.
    /// </summary>
    InvalidChallenge = 2,
}
