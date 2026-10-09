namespace TemplateName.Modules.Auth.Domain.Sessions;

/// <summary>Why a session ended. Persisted by its numeric value, so a new reason is appended and existing values are never renumbered.</summary>
internal enum SessionRevokedReason
{
    Logout = 0,
    LogoutAll = 1,
    PasswordChanged = 2,
    AdminRevoked = 3,
    TokenReuse = 4,
    Expired = 5,
}
