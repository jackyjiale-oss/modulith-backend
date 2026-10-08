using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Passwords.Reset;

/// <summary>Sets a new password with the token from the emailed reset link.</summary>
internal sealed record ResetPasswordCommand(string Token, string NewPassword) : ICommand
{
    /// <summary>Only the type name, so a logged or formatted command can never carry the token or the password.</summary>
    public override string ToString() => nameof(ResetPasswordCommand);
}
