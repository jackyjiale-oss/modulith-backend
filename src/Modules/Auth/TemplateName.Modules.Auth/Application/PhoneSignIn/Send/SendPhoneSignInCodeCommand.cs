using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.PhoneSignIn.Send;

/// <summary>Texts a sign-in code to <paramref name="PhoneNumber"/> when it is an account's verified number. Prints its type name only.</summary>
internal sealed record SendPhoneSignInCodeCommand(string PhoneNumber) : ICommand
{
    public override string ToString() => nameof(SendPhoneSignInCodeCommand);
}
