using System.Globalization;
using FluentValidation;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Registration.Register;

/// <summary>
/// Runs in the validation decorator, before the handler, so a password outside the length limits (a 1 MB string included) is refused
/// before any breach lookup or hashing. Each rule stops at its first failure, so an over-long value never reaches a later check.
/// </summary>
internal sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator(IOptions<PasswordOptions> passwordOptions)
    {
        var passwords = passwordOptions.Value;

        RuleFor(command => command.Email)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxEmailLength)
            .EmailAddress();

        // Lengths count UTF-16 characters, as everywhere else in the module; NotEmpty also refuses a password of only white space.
        RuleFor(command => command.Password)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Length(passwords.MinLength, passwords.MaxLength)
            .Must((command, password) => !IsEmail(password, command.Email))
            .WithMessage("'{PropertyName}' must not be the same as the email address.");

        RuleFor(command => command.DisplayName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxDisplayNameLength);

        RuleFor(command => command.Locale)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxLocaleLength)
            .Must(IsPredefinedCulture)
            .WithMessage("'{PropertyName}' must be a known culture name, such as en, ms or zh-Hans.");
    }

    private static bool IsEmail(string password, string? email)
        => email is not null && string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsPredefinedCulture(string locale)
    {
        try
        {
            return CultureInfo.GetCultureInfo(locale, predefinedOnly: true).Name.Length > 0;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
