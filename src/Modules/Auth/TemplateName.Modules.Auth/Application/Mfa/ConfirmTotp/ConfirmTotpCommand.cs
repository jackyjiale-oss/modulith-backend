using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Mfa.ConfirmTotp;

/// <summary>Confirms the signed-in user's pending authenticator setup with a <paramref name="Code"/> from the app, which turns two-step verification on.</summary>
internal sealed record ConfirmTotpCommand(string Code) : ICommand<RecoveryCodesResponse>
{
    /// <summary>Only the type name, so a logged or formatted command can never carry the code.</summary>
    public override string ToString() => nameof(ConfirmTotpCommand);
}
