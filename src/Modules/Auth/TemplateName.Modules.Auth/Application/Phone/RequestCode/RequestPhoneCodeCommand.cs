using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Phone.RequestCode;

/// <summary>Sends a verification code by SMS to <see cref="PhoneNumber"/>, the number the caller wants on the account.</summary>
/// <param name="PhoneNumber">The number in international form (it starts with <c>+</c>); spaces, dashes and brackets are allowed.</param>
internal sealed record RequestPhoneCodeCommand(string PhoneNumber) : ICommand<PhoneCodeResponse>
{
    /// <summary>Only the type name, so a logged or formatted command never carries the number.</summary>
    public override string ToString() => nameof(RequestPhoneCodeCommand);
}
