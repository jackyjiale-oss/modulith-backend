using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Authentication;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.Login;

/// <summary>
/// Signs in, or signs up, with a proof from the provider <see cref="Provider"/> (the route value, matched without regard to case): an ID
/// token with the raw nonce it binds, or (Facebook only) an access token. The answer is a <see cref="LoginResult"/>: the tokens of a new
/// session, or, for an account with two-step verification, a challenge whose first factor is <c>fed</c>. <see cref="DisplayName"/> and
/// <see cref="Locale"/> are used only when an account is created (the request's language when no locale is given);
/// <see cref="DeviceName"/> names the session (<c>Unknown</c> when none is given).
/// </summary>
internal sealed record ExternalLoginCommand(
    string Provider,
    string? IdToken,
    string? AccessToken,
    string? Nonce,
    string? DisplayName,
    string? Locale,
    string? DeviceName) : ICommand<LoginResult>
{
    /// <summary>Only the type name, so a logged or formatted command can never carry a token or the nonce.</summary>
    public override string ToString() => nameof(ExternalLoginCommand);
}
