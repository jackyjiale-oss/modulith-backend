using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.StepUp;

/// <summary>
/// Proves the signed-in user's identity again, with exactly one of: the password (an account without two-step verification), a code
/// from the authenticator app or a recovery code (an account with it), or a social provider's proof (<paramref name="Provider"/> with an
/// <paramref name="IdToken"/> and its <paramref name="Nonce"/>, or, for Facebook, an <paramref name="AccessToken"/>; an account with
/// neither a password nor two-step verification). The provider's members count together as one proof.
/// </summary>
internal sealed record StepUpCommand(
    string? Password,
    string? Code,
    string? RecoveryCode,
    string? Provider = null,
    string? IdToken = null,
    string? AccessToken = null,
    string? Nonce = null) : ICommand<StepUpResponse>
{
    /// <summary>
    /// Whether any member of the provider's proof is present (a blank value is absent). Any one of them makes the request a provider proof,
    /// so a stray <c>nonce</c> next to a password is "more than one proof", never silently ignored.
    /// </summary>
    internal bool HasProviderProof =>
        !string.IsNullOrWhiteSpace(Provider)
        || !string.IsNullOrWhiteSpace(IdToken)
        || !string.IsNullOrWhiteSpace(AccessToken)
        || !string.IsNullOrWhiteSpace(Nonce);

    /// <summary>Only the type name, so a logged or formatted command can never carry the password, a code or a provider's proof.</summary>
    public override string ToString() => nameof(StepUpCommand);
}
