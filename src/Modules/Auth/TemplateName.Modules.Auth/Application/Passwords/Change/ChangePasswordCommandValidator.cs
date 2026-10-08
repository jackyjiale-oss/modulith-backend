using FluentValidation;
using Microsoft.Extensions.Options;

namespace TemplateName.Modules.Auth.Application.Passwords.Change;

/// <summary>
/// Runs in the validation decorator, before the handler, so neither password can cost hashing time when it is over-long (a 1 MB string
/// included). The new password has the limits of registration; the current one, as at login, has no minimum, because a password an older,
/// shorter minimum accepted must still prove the caller (Ruling R14: UTF-16 characters, not trimmed, not normalized).
/// </summary>
internal sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator(IOptions<PasswordOptions> passwordOptions)
    {
        var passwords = passwordOptions.Value;

        RuleFor(command => command.CurrentPassword)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(passwords.MaxLength);

        // NotEmpty also refuses a password of only white space.
        RuleFor(command => command.NewPassword)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Length(passwords.MinLength, passwords.MaxLength);
    }
}
