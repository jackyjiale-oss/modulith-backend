using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Registration.ConfirmEmail;

/// <summary>Confirms an email address with the token from the emailed link.</summary>
internal sealed record ConfirmEmailCommand(string Token) : ICommand
{
    /// <summary>Only the type name, so a logged or formatted command can never carry the token.</summary>
    public override string ToString() => nameof(ConfirmEmailCommand);
}
