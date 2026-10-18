using TemplateName.Modules.Auth.Application.Authentication.Login;
using TemplateName.Modules.Auth.Domain.Sessions;

namespace TemplateName.Modules.Auth.Application.Authentication;

/// <summary>A session <see cref="SessionStarter"/> added (not saved yet) and the tokens to answer with once the caller's save succeeds.</summary>
/// <param name="Session">The new session, tracked for the caller's save.</param>
/// <param name="Tokens">The access token, the first refresh token (shown once) and their expiries.</param>
internal sealed record StartedSession(UserSession Session, LoginResponse Tokens)
{
    /// <summary>Only the type name and the session, so a logged value can never carry a token.</summary>
    public override string ToString() => $"{nameof(StartedSession)} {{ SessionId = {Session.Id} }}";
}
