using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Sessions;

/// <summary>The session and refresh-token errors. Each code has a message in <c>Resources/AuthErrorMessages.resx</c> and its translations.</summary>
internal static class SessionErrors
{
    public static readonly Error InvalidRefreshToken = Error.Unauthorized(
        "auth.invalid_refresh_token",
        "The refresh token is not valid.");

    public static readonly Error RefreshTokenExpired = Error.Unauthorized(
        "auth.refresh_token_expired",
        "The refresh token has expired. Sign in again.");

    public static readonly Error RefreshTokenReused = Error.Unauthorized(
        "auth.refresh_token_reused",
        "The refresh token was already used. The session has been ended; sign in again.");

    public static Error NotFound(Guid id) =>
        Error.NotFound("auth.session_not_found", $"Session '{id}' was not found.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };
}
