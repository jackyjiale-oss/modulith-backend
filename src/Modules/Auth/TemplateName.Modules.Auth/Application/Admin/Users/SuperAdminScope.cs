using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users;

/// <summary>The SuperAdmin role of this database and whether the acting administrator holds it (see <see cref="SuperAdminRules"/>).</summary>
/// <param name="RoleId">The id of the seeded SuperAdmin role.</param>
/// <param name="ActorIsSuperAdmin">Whether the acting administrator is an active SuperAdmin.</param>
internal sealed record SuperAdminScope(Guid RoleId, bool ActorIsSuperAdmin)
{
    public bool IsHeldBy(User user) => user.Roles.Any(assignment => assignment.RoleId == RoleId);

    /// <summary>Fails with <see cref="UserErrors.CannotManageSuperAdmin"/> when the target is a SuperAdmin and the actor is not.</summary>
    public Result EnsureCanManage(User target)
        => IsHeldBy(target) && !ActorIsSuperAdmin ? Result.Failure(UserErrors.CannotManageSuperAdmin) : Result.Success();

    /// <summary>Fails with <see cref="UserErrors.CannotManageSuperAdmin"/> when the roles include SuperAdmin and the actor is not one.</summary>
    public Result EnsureCanGrant(IEnumerable<Guid> roleIds)
        => roleIds.Contains(RoleId) && !ActorIsSuperAdmin ? Result.Failure(UserErrors.CannotManageSuperAdmin) : Result.Success();
}
