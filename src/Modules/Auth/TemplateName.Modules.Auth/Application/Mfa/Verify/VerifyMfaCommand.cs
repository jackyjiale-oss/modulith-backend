using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Mfa.Verify;

/// <summary>
/// Completes a sign-in that login answered with a challenge: the challenge token and exactly one of a code from the authenticator app
/// or a recovery code. With <see cref="RememberDevice"/> (and <c>Auth:Mfa:TrustedDevicesEnabled</c> on) the device is also remembered:
/// the answer carries a trusted device token that skips the challenge at the next sign-ins (ADR 0025).
/// </summary>
internal sealed record VerifyMfaCommand(string ChallengeToken, string? Code, string? RecoveryCode, bool RememberDevice) : ICommand<MfaVerifyResponse>
{
    /// <summary>Only the type name, so a logged or formatted command can never carry the challenge or a code.</summary>
    public override string ToString() => nameof(VerifyMfaCommand);
}
