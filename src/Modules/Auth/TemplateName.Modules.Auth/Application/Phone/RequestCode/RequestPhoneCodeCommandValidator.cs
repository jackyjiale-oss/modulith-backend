using FluentValidation;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Phone.RequestCode;

/// <summary>Only the shape: whether the number is a valid mobile number is the handler's answer (<c>auth.invalid_phone_number</c>).</summary>
internal sealed class RequestPhoneCodeCommandValidator : AbstractValidator<RequestPhoneCodeCommand>
{
    public RequestPhoneCodeCommandValidator()
    {
        RuleFor(command => command.PhoneNumber)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxPhoneNumberLength);
    }
}
