using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Authentication.Login;

/// <summary>
/// Signs in with an email and a password on the named device (<c>Unknown</c> when none is given): a new session, or, for an account with
/// two-step verification, a challenge that <c>POST /api/v1/auth/mfa/verify</c> completes. <see cref="TrustedDeviceToken"/> is the token
/// of a device remembered at an earlier verify: while it is usable, an account with two-step verification gets its session without the
/// challenge; otherwise it is ignored.
/// </summary>
internal sealed record LoginCommand(string Email, string Password, string? DeviceName, string? TrustedDeviceToken) : ICommand<LoginResult>
{
    /// <summary>Only the type name, so a logged or formatted command can never carry the password or the device token.</summary>
    public override string ToString() => nameof(LoginCommand);
}
