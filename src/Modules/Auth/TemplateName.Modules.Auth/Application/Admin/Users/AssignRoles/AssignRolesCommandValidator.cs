using FluentValidation;

namespace TemplateName.Modules.Auth.Application.Admin.Users.AssignRoles;

internal sealed class AssignRolesCommandValidator : AbstractValidator<AssignRolesCommand>
{
    /// <summary>The most roles one request may name; far more than any account needs, and it bounds the lookup.</summary>
    internal const int MaxRoles = 50;

    public AssignRolesCommandValidator()
    {
        // An empty set is allowed: it removes every role.
        RuleFor(command => command.RoleIds)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(roleIds => roleIds.Count <= MaxRoles)
            .WithMessage($"'{{PropertyName}}' may hold at most {MaxRoles} role ids.")
            .Must(roleIds => !roleIds.Contains(Guid.Empty))
            .WithMessage("'{PropertyName}' must not contain an empty id.");
    }
}
