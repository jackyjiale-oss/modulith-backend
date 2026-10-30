using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.Link;

/// <summary>
/// Links the identity a proof vouches for at the provider <see cref="Provider"/> (the route value, matched without regard to case) to the
/// signed-in user: an ID token with the raw nonce it binds, or (Facebook only) an access token. The answer is the login, new or already
/// there.
/// </summary>
internal sealed record LinkExternalLoginCommand(string Provider, string? IdToken, string? AccessToken, string? Nonce) : ICommand<ExternalLoginResponse>
{
    /// <summary>Only the type name, so a logged or formatted command can never carry a token or the nonce.</summary>
    public override string ToString() => nameof(LinkExternalLoginCommand);
}
