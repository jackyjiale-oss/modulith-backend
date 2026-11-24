using FluentValidation;

namespace TemplateName.Modules.Auth.Application.Phone.Confirm;

/// <summary>A code is exactly six ASCII digits; anything else is refused before a try is counted.</summary>
internal sealed class ConfirmPhoneCommandValidator : AbstractValidator<ConfirmPhoneCommand>
{
    public ConfirmPhoneCommandValidator()
    {
        RuleFor(command => command.Code)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Matches(@"^[0-9]{6}\z");
    }
}
