namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>How a login attempt ended, as counted by <see cref="IAuthMetrics.RecordLogin"/>.</summary>
internal enum LoginOutcome
{
    /// <summary>Tokens were issued.</summary>
    Succeeded = 0,

    /// <summary>Unknown email, no password set, wrong password (also while locked), or a lost save race: the generic 401.</summary>
    InvalidCredentials = 1,

    /// <summary>The password was correct but the account is locked out; the caller still got the generic 401 (decision D10).</summary>
    Locked = 2,

    /// <summary>The password was correct but the email address is not confirmed (403).</summary>
    Unverified = 3,

    /// <summary>The password was correct but the account is suspended (403).</summary>
    Inactive = 4,
}
