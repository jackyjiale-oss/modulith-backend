using FluentValidation;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.Update;

internal sealed class UpdateRoleCommandValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleCommandValidator()
    {
        RuleFor(command => command.Name).MustBeRoleName();
        RuleFor(command => command.Description).MustBeRoleDescription();
    }
}
