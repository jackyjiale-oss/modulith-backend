using FluentValidation;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Application.Admin.Roles;

/// <summary>The rules for a role's name and description, shared by the create and update validators.</summary>
internal static class RoleRules
{
    /// <summary>A name that is not blank and fits its column.</summary>
    public static IRuleBuilderOptions<T, string> MustBeRoleName<T>(this IRuleBuilderInitial<T, string> rule)
        => rule.Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(Role.MaxNameLength);

    /// <summary>A description that is present (it may be empty) and fits its column.</summary>
    public static IRuleBuilderOptions<T, string> MustBeRoleDescription<T>(this IRuleBuilderInitial<T, string> rule)
        => rule.Cascade(CascadeMode.Stop).NotNull().MaximumLength(Role.MaxDescriptionLength);
}
