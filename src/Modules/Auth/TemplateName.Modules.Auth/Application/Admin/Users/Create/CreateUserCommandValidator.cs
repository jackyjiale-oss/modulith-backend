using FluentValidation;
using TemplateName.Modules.Auth.Application.Admin.Users.AssignRoles;
using TemplateName.Modules.Auth.Application.Me.Update;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Admin.Users.Create;

/// <summary>The rules of registration for the email, display name and locale (no password: the user sets one through the reset link).</summary>
internal sealed class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(command => command.Email)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxEmailLength)
            .EmailAddress();

        RuleFor(command => command.DisplayName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxDisplayNameLength);

        RuleFor(command => command.Locale)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxLocaleLength)
            .Must(locale => UpdateProfileCommandValidator.TryGetCanonicalLocale(locale, out _))
            .WithMessage("'{PropertyName}' must be a known culture name, such as en, ms or zh-Hans.");

        RuleFor(command => command.RoleIds)
            .Must(roleIds => roleIds!.Count <= AssignRolesCommandValidator.MaxRoles)
            .WithMessage($"'{{PropertyName}}' may hold at most {AssignRolesCommandValidator.MaxRoles} role ids.")
            .Must(roleIds => !roleIds!.Contains(Guid.Empty))
            .WithMessage("'{PropertyName}' must not contain an empty id.")
            .When(command => command.RoleIds is not null);
    }
}
