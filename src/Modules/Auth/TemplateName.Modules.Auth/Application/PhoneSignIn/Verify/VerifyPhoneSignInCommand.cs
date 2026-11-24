using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Authentication;

namespace TemplateName.Modules.Auth.Application.PhoneSignIn.Verify;

/// <summary>Trades a texted sign-in code for tokens or a two-step verification challenge. Prints its type name only.</summary>
internal sealed record VerifyPhoneSignInCommand(string PhoneNumber, string Code, string? DeviceName) : ICommand<LoginResult>
{
    public override string ToString() => nameof(VerifyPhoneSignInCommand);
}
