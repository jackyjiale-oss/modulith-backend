using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.StepUp;

/// <summary>
/// Proves the signed-in user's identity again, with exactly one of: the password (an account without two-step verification), a code
/// from the authenticator app or a recovery code (an account with it).
/// </summary>
internal sealed record StepUpCommand(string? Password, string? Code, string? RecoveryCode) : ICommand<StepUpResponse>
{
    /// <summary>Only the type name, so a logged or formatted command can never carry the password or a code.</summary>
    public override string ToString() => nameof(StepUpCommand);
}
