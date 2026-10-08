using FluentValidation;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Authentication.Login;

/// <summary>
/// Runs in the validation decorator, before the handler, so an over-long password (a 1 MB string included) is refused before any lookup
/// or hashing. The answer depends on the request alone, never on an account. The password has no minimum here: a password an older,
/// shorter minimum accepted must still sign in, and only the hash can tell whether it is right.
/// </summary>
internal sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator(IOptions<PasswordOptions> passwordOptions)
    {
        var passwords = passwordOptions.Value;

        RuleFor(command => command.Email)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxEmailLength)
            .EmailAddress();

        // UTF-16 characters, as everywhere in the module (Ruling R14); not trimmed and not normalized.
        RuleFor(command => command.Password)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(passwords.MaxLength);

        RuleFor(command => command.DeviceName)
            .MaximumLength(UserSession.MaxDeviceNameLength);
    }
}
