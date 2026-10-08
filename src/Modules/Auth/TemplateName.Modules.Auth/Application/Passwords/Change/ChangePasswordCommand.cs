using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Passwords.Change;

/// <summary>Replaces the signed-in user's password, proven with the current one.</summary>
internal sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword) : ICommand
{
    /// <summary>Only the type name, so a logged or formatted command can never carry either password.</summary>
    public override string ToString() => nameof(ChangePasswordCommand);
}
