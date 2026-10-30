namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>How an external sign-in that reached the handler ended, as counted by <see cref="IAuthMetrics.RecordExternalLogin"/>.</summary>
internal enum ExternalLoginOutcome
{
    /// <summary>Tokens were issued.</summary>
    Succeeded = 0,

    /// <summary>The proof was good and the account has two-step verification: a challenge was issued instead of tokens.</summary>
    MfaRequired = 1,

    /// <summary>The provider is unknown or disabled (404).</summary>
    ProviderNotFound = 2,

    /// <summary>The proof failed verification (401).</summary>
    InvalidToken = 3,

    /// <summary>The provider could not be asked (503).</summary>
    ProviderUnavailable = 4,

    /// <summary>An identity that is not linked came without a usable email (403).</summary>
    EmailUnverified = 5,

    /// <summary>An account with the email exists and cannot be linked automatically (409).</summary>
    LinkRequired = 6,

    /// <summary>No account, and sign-up is off (403).</summary>
    SignUpDisabled = 7,

    /// <summary>The account is suspended (403).</summary>
    Inactive = 8,

    /// <summary>The account is locked out; the caller got the generic 401.</summary>
    Locked = 9,

    /// <summary>The generic 401 for anything else: a lost save race, or an identity or email a deleted account still holds.</summary>
    InvalidCredentials = 10,
}
