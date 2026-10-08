using FluentValidation;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Registration.ConfirmEmail;

namespace TemplateName.Modules.Auth.Application.Passwords.Reset;

/// <summary>
/// Runs in the validation decorator, before the handler, so a password outside the length limits (a 1 MB string included) is refused
/// before any lookup, breach check or hashing. The limits are those of registration (Ruling R14: UTF-16 characters, not normalized).
/// </summary>
internal sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator(IOptions<PasswordOptions> passwordOptions)
    {
        var passwords = passwordOptions.Value;

        RuleFor(command => command.Token)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(ConfirmEmailCommandValidator.MaxTokenLength);

        // NotEmpty also refuses a password of only white space; the password itself is never trimmed.
        RuleFor(command => command.NewPassword)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Length(passwords.MinLength, passwords.MaxLength);
    }
}
