using FluentValidation;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.PhoneSignIn.Send;

internal sealed class SendPhoneSignInCodeCommandValidator : AbstractValidator<SendPhoneSignInCodeCommand>
{
    public SendPhoneSignInCodeCommandValidator()
    {
        RuleFor(command => command.PhoneNumber)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxPhoneNumberLength);
    }
}
