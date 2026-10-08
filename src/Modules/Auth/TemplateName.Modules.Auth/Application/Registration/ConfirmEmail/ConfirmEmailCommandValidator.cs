using FluentValidation;

namespace TemplateName.Modules.Auth.Application.Registration.ConfirmEmail;

internal sealed class ConfirmEmailCommandValidator : AbstractValidator<ConfirmEmailCommand>
{
    /// <summary>A real token is 43 characters; the limit only keeps an absurd value away from hashing.</summary>
    internal const int MaxTokenLength = 256;

    public ConfirmEmailCommandValidator()
    {
        RuleFor(command => command.Token)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(MaxTokenLength);
    }
}
