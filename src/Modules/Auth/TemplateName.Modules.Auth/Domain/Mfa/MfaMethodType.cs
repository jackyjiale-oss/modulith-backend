namespace TemplateName.Modules.Auth.Domain.Mfa;

/// <summary>The kind of second factor a user can set up. Persisted by its numeric value, so a new kind is appended and existing values are never renumbered.</summary>
internal enum MfaMethodType
{
    /// <summary>A time-based one-time password from an authenticator app (RFC 6238).</summary>
    Totp = 1,
}
