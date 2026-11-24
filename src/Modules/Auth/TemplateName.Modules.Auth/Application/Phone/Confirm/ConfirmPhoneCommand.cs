using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Phone.Confirm;

/// <summary>Verifies the caller's pending number with the code texted to it.</summary>
/// <param name="Code">The 6 digits from the text.</param>
internal sealed record ConfirmPhoneCommand(string Code) : ICommand
{
    /// <summary>Only the type name, so a logged or formatted command never carries the code.</summary>
    public override string ToString() => nameof(ConfirmPhoneCommand);
}
