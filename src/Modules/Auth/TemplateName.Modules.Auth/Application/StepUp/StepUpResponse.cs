namespace TemplateName.Modules.Auth.Application.StepUp;

/// <summary>
/// A new access token of the same session, whose <c>auth_time</c> is the step-up and whose <c>amr</c> is what it proved. The refresh
/// token is unchanged; refreshed tokens keep both claims.
/// </summary>
/// <param name="AccessToken">The ES256 JWT that replaces the caller's current access token.</param>
/// <param name="AccessTokenExpiresAt">The access token's <c>exp</c>.</param>
/// <param name="StepUpExpiresAt">When the step-up stops satisfying a protected action: the token's <c>auth_time</c> (whole seconds) plus <c>Auth:StepUp:MaxAge</c>.</param>
internal sealed record StepUpResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, DateTimeOffset StepUpExpiresAt)
{
    /// <summary>Only the type name, so a logged response can never carry the token.</summary>
    public override string ToString() => nameof(StepUpResponse);
}
