using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.SetPermissions;

/// <summary>
/// Replaces a role's permissions with the requested set (duplicates ignored; an empty set removes every grant). The permissions of
/// <c>SuperAdmin</c> are never edited by hand (<see cref="RoleErrors.SystemRoleProtected"/>, decided by <see cref="Role.SetPermissions"/>);
/// those of <c>Admin</c>, <c>User</c> and every custom role can be. Checks, in order:
/// <list type="number">
/// <item>Every id must name a permission (<see cref="RoleErrors.PermissionNotFound"/>).</item>
/// <item>A deprecated permission cannot be newly granted (the same answer: it no longer exists as far as a grant is concerned); one the
/// role already holds may stay in the set, so the set a client read can be written back.</item>
/// <item>An administrator can newly grant only what they hold themselves (<see cref="RoleErrors.PermissionGrantNotAllowed"/>).
/// Without it, whoever holds <c>auth.role.manage</c> could put any permission on a role, assign the role to themselves or let a
/// colleague do so, and so hold what they were never given. A SuperAdmin holds every permission that is not deprecated, so the rule
/// never stops one. Withdrawing a permission, or leaving one in place, escalates nothing and is not checked.</item>
/// </list>
/// A set equal to the current one saves and audits nothing. Otherwise one save writes the grants and the audit entry
/// (<c>auth.role_permissions_changed</c>, listing the codes added and removed), and after it the cached permissions of every user in
/// the role are removed, so the change applies to their next request on this instance.
/// </summary>
internal sealed class SetRolePermissionsCommandHandler(
    IRoleRepository roles,
    IPermissionRepository permissions,
    IPermissionChecker permissionChecker,
    RolePermissionCacheInvalidator cacheInvalidator,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<SetRolePermissionsCommand>
{
    public async Task<Result> HandleAsync(SetRolePermissionsCommand command, CancellationToken cancellationToken)
    {
        var role = await roles.GetByIdAsync(command.RoleId, cancellationToken);
        if (role is null)
        {
            return Result.Failure(RoleErrors.NotFound(command.RoleId));
        }

        var requested = command.PermissionIds.Distinct().ToList();
        var current = role.Permissions.Select(grant => grant.PermissionId).ToHashSet();
        var added = requested.Where(id => !current.Contains(id)).ToList();
        var removed = current.Where(id => !requested.Contains(id)).ToList();

        var known = (await permissions.GetByIdsAsync([.. requested, .. removed], cancellationToken)).ToDictionary(permission => permission.Id);
        if (requested.Exists(id => !known.ContainsKey(id)) || added.Exists(id => known[id].IsDeprecated))
        {
            return Result.Failure(RoleErrors.PermissionNotFound);
        }

        foreach (var id in added)
        {
            if (!await permissionChecker.HasPermissionAsync(command.ActorId, known[id].Code, cancellationToken))
            {
                return Result.Failure(RoleErrors.PermissionGrantNotAllowed);
            }
        }

        // The domain refuses SuperAdmin, also when the set would not change.
        var set = role.SetPermissions(requested);
        if (set.IsFailure)
        {
            return set;
        }

        if (added.Count == 0 && removed.Count == 0)
        {
            return Result.Success();
        }

        auditWriter.Record(RoleAudit.PermissionsChanged(
            command.ActorId,
            role,
            CodesOf(added, known),
            CodesOf(removed, known),
            timeProvider.GetUtcNow()));

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await cacheInvalidator.InvalidateAsync(role.Id);

        return Result.Success();
    }

    private static List<string> CodesOf(IEnumerable<Guid> ids, Dictionary<Guid, Permission> known)
        => [.. ids.Where(known.ContainsKey).Select(id => known[id].Code).Order(StringComparer.Ordinal)];
}
