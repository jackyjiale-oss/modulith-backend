using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Registration.Register;

/// <summary>Creates an account and sends its confirmation link, or tells the owner of an existing account; both answer the same.</summary>
internal sealed record RegisterCommand(string Email, string Password, string DisplayName, string Locale) : ICommand
{
    /// <summary>Only the type name, so a logged or formatted command can never carry the password.</summary>
    public override string ToString() => nameof(RegisterCommand);
}
