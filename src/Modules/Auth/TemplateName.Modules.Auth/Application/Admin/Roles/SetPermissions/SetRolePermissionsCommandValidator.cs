using FluentValidation;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.SetPermissions;

internal sealed class SetRolePermissionsCommandValidator : AbstractValidator<SetRolePermissionsCommand>
{
    /// <summary>The most ids one request may name; far more than the permissions of any deployment, and it bounds the lookup.</summary>
    internal const int MaxPermissions = 500;

    public SetRolePermissionsCommandValidator()
    {
        // An empty set is allowed: it removes every permission.
        RuleFor(command => command.PermissionIds)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(permissionIds => permissionIds.Count <= MaxPermissions)
            .WithMessage($"'{{PropertyName}}' may hold at most {MaxPermissions} permission ids.")
            .Must(permissionIds => !permissionIds.Contains(Guid.Empty))
            .WithMessage("'{PropertyName}' must not contain an empty id.");
    }
}
