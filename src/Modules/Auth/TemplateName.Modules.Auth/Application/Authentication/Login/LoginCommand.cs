using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Authentication.Login;

/// <summary>Signs in with an email and a password and starts a session on the named device (<c>Unknown</c> when none is given).</summary>
internal sealed record LoginCommand(string Email, string Password, string? DeviceName) : ICommand<LoginResponse>
{
    /// <summary>Only the type name, so a logged or formatted command can never carry the password.</summary>
    public override string ToString() => nameof(LoginCommand);
}
