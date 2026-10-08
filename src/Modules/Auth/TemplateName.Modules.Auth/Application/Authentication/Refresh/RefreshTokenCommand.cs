using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Authentication.Login;

namespace TemplateName.Modules.Auth.Application.Authentication.Refresh;

/// <summary>Exchanges a refresh token for a new access token and the next refresh token of the same session.</summary>
internal sealed record RefreshTokenCommand(string RefreshToken) : ICommand<LoginResponse>
{
    /// <summary>Only the type name, so a logged or formatted command can never carry the token.</summary>
    public override string ToString() => nameof(RefreshTokenCommand);
}
