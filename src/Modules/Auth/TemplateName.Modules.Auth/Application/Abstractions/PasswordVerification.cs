namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>The outcome of checking a password against a stored hash.</summary>
internal enum PasswordVerification
{
    /// <summary>The password does not match, or the stored hash is not a valid hash.</summary>
    Failed = 0,

    /// <summary>The password matches.</summary>
    Success = 1,

    /// <summary>The password matches but the hash was made with weaker parameters; store a new hash.</summary>
    SuccessRehashNeeded = 2,
}
