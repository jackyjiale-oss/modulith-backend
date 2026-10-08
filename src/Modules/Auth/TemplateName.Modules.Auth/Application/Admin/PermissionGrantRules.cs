using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin;

/// <summary>
/// The no-escalation rule of the administration: an administrator can newly grant only permissions they hold themselves, whether by
/// adding a permission to a role or by giving a user a role. Without it, whoever holds <c>auth.role.manage</c> or
/// <c>auth.user.assign_roles</c> could hold, or hand out, what they were never given. A SuperAdmin holds every permission that is not
/// deprecated, so the rule never stops one. The check uses the actor's cached permission set (<see cref="IPermissionChecker"/>, at most
/// 30 seconds old on other instances, ADR 0016).
/// </summary>
internal sealed class PermissionGrantRules(IPermissionRepository permissions, IPermissionChecker permissionChecker)
{
    /// <summary>
    /// Fails with <see cref="RoleErrors.PermissionGrantNotAllowed"/> at the first of <paramref name="permissionCodes"/>, in the order
    /// given, that <paramref name="actorId"/> does not hold.
    /// </summary>
    public async Task<Result> EnsureActorHoldsAsync(Guid actorId, IEnumerable<string> permissionCodes, CancellationToken cancellationToken)
    {
        foreach (var code in permissionCodes)
        {
            if (!await permissionChecker.HasPermissionAsync(actorId, code, cancellationToken))
            {
                return Result.Failure(RoleErrors.PermissionGrantNotAllowed);
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Fails with <see cref="RoleErrors.PermissionGrantNotAllowed"/> unless <paramref name="actorId"/> holds every permission that one of
    /// <paramref name="newlyGrantedRoles"/> grants. Deprecated permissions grant nothing (the checker never grants them either), so they
    /// are not required. Pass only the roles a user does not hold yet: keeping or removing a role escalates nothing.
    /// </summary>
    public async Task<Result> EnsureActorHoldsRolesAsync(Guid actorId, IEnumerable<Role> newlyGrantedRoles, CancellationToken cancellationToken)
    {
        var permissionIds = newlyGrantedRoles.SelectMany(role => role.Permissions).Select(grant => grant.PermissionId).Distinct().ToList();
        if (permissionIds.Count == 0)
        {
            return Result.Success();
        }

        var codes = (await permissions.GetByIdsAsync(permissionIds, cancellationToken))
            .Where(permission => !permission.IsDeprecated)
            .Select(permission => permission.Code)
            .Order(StringComparer.Ordinal);

        return await EnsureActorHoldsAsync(actorId, codes, cancellationToken);
    }
}
