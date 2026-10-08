using FluentValidation;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.Create;

internal sealed class CreateRoleCommandValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleCommandValidator()
    {
        RuleFor(command => command.Name).MustBeRoleName();
        RuleFor(command => command.Description).MustBeRoleDescription();
    }
}
