using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users;

/// <summary>
/// The rules that keep SuperAdmin accounts out of reach of lesser administrators. The <c>Admin</c> role holds <c>auth.user.lock</c>,
/// <c>auth.user.reset_password</c> and <c>auth.user.revoke_sessions</c> by default, so without them an Admin could lock out, sign out or
/// take over (through a forced reset) every SuperAdmin; and whoever may assign roles could make themselves SuperAdmin. Only an active
/// SuperAdmin may act on a SuperAdmin account or grant the role, and the last active SuperAdmin can be neither locked nor demoted.
/// </summary>
internal sealed class SuperAdminRules(IRoleRepository roles, IUserRepository users)
{
    /// <summary>Finds the SuperAdmin role and whether <paramref name="actorId"/> is an active SuperAdmin.</summary>
    /// <exception cref="InvalidOperationException">The SuperAdmin role is missing: the module has not been seeded.</exception>
    public async Task<SuperAdminScope> GetScopeAsync(Guid actorId, CancellationToken cancellationToken)
    {
        var role = await roles.GetByNormalizedNameAsync(Role.NormalizeName(SystemRoles.SuperAdmin), cancellationToken);
        if (role is not { IsSuperAdmin: true })
        {
            throw new InvalidOperationException("The SuperAdmin system role does not exist; run the Auth module seeder (SeedAuthModuleAsync).");
        }

        var actorIsSuperAdmin = await users.IsActiveInRoleAsync(actorId, role.Id, cancellationToken);
        return new SuperAdminScope(role.Id, actorIsSuperAdmin);
    }

    /// <summary>
    /// Fails with <see cref="UserErrors.LastSuperAdmin"/> when <paramref name="target"/> is an active SuperAdmin and no other active,
    /// undeleted user holds the role, so locking or demoting it would leave none. A suspended SuperAdmin is not one that counts.
    /// </summary>
    public async Task<Result> EnsureNotLastActiveAsync(SuperAdminScope scope, User target, CancellationToken cancellationToken)
    {
        if (!scope.IsHeldBy(target) || target.Status != UserStatus.Active)
        {
            return Result.Success();
        }

        return await users.AnyOtherActiveInRoleAsync(scope.RoleId, target.Id, cancellationToken)
            ? Result.Success()
            : Result.Failure(UserErrors.LastSuperAdmin);
    }
}
