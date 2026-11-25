using FluentValidation;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.PhoneSignIn.Verify;

internal sealed class VerifyPhoneSignInCommandValidator : AbstractValidator<VerifyPhoneSignInCommand>
{
    public VerifyPhoneSignInCommandValidator()
    {
        RuleFor(command => command.PhoneNumber)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxPhoneNumberLength);

        RuleFor(command => command.Code)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Matches(@"^[0-9]{6}\z");

        RuleFor(command => command.DeviceName).MaximumLength(UserSession.MaxDeviceNameLength);
    }
}
