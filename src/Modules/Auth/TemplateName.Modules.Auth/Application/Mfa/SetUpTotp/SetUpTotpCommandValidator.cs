using FluentValidation;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Application.Mfa.SetUpTotp;

/// <summary>
/// Runs in the validation decorator, before the handler: a label may be absent (the handler names the method <c>Authenticator app</c>),
/// but one that is given is not blank and has at most <see cref="UserMfaMethod.MaxLabelLength"/> characters.
/// </summary>
internal sealed class SetUpTotpCommandValidator : AbstractValidator<SetUpTotpCommand>
{
    public SetUpTotpCommandValidator()
    {
        // NotEmpty also refuses a label of only white space.
        RuleFor(command => command.Label)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(UserMfaMethod.MaxLabelLength)
            .When(command => command.Label is not null);
    }
}
