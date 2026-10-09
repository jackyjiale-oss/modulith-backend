using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users.AssignRoles;

/// <summary>
/// Replaces a user's roles with the requested set (duplicates ignored; an empty set removes every role). Every id must be a role that
/// exists and is not deleted (<see cref="RoleErrors.NotFound"/> names the first one that is not). Only a SuperAdmin can change the roles
/// of a SuperAdmin or grant SuperAdmin, to anyone including themselves. A role the user does not hold yet can be given only by an actor
/// who holds every permission it grants (<see cref="PermissionGrantRules"/>, <see cref="RoleErrors.PermissionGrantNotAllowed"/>), so
/// <c>auth.user.assign_roles</c> cannot hand out more than its holder has. The last active SuperAdmin cannot lose the role
/// (<see cref="UserErrors.LastSuperAdmin"/>). After the save the user's cached permissions are removed, so the change applies to their
/// next request on this instance (ADR 0016; other instances within 30 seconds).
/// </summary>
internal sealed class AssignRolesCommandHandler(
    IUserRepository users,
    IRoleRepository roles,
    SuperAdminRules superAdminRules,
    PermissionGrantRules grantRules,
    IPermissionCache permissionCache,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<AssignRolesCommand>
{
    public async Task<Result> HandleAsync(AssignRolesCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserErrors.NotFound(command.UserId));
        }

        var scope = await superAdminRules.GetScopeAsync(command.ActorId, cancellationToken);
        var allowed = scope.EnsureCanManage(user);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var requested = command.RoleIds.Distinct().ToList();
        var found = (await roles.GetByIdsAsync(requested, cancellationToken)).ToDictionary(role => role.Id);
        if (requested.Where(id => !found.ContainsKey(id)).Select(id => (Guid?)id).FirstOrDefault() is { } unknownId)
        {
            return Result.Failure(RoleErrors.NotFound(unknownId));
        }

        allowed = scope.EnsureCanGrant(requested);
        if (allowed.IsSuccess)
        {
            // Only the roles the user does not hold yet grant anything new; keeping or removing a role escalates nothing.
            var current = user.Roles.Select(assignment => assignment.RoleId).ToHashSet();
            var newlyGranted = requested.Where(id => !current.Contains(id)).Select(id => found[id]);
            allowed = await grantRules.EnsureActorHoldsRolesAsync(command.ActorId, newlyGranted, cancellationToken);
        }

        if (allowed.IsSuccess && !requested.Contains(scope.RoleId))
        {
            allowed = await superAdminRules.EnsureNotLastActiveAsync(scope, user, cancellationToken);
        }

        if (allowed.IsFailure)
        {
            return allowed;
        }

        var now = timeProvider.GetUtcNow();
        foreach (var removed in user.Roles.Select(assignment => assignment.RoleId).Except(requested).ToList())
        {
            user.RemoveRole(removed);
        }

        foreach (var roleId in requested)
        {
            user.AssignRole(roleId, command.ActorId, now);
        }

        auditWriter.Record(AdminAudit.Succeeded(AuthAuditEvents.AdminRolesAssigned, user.Id, command.ActorId, now));

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await permissionCache.InvalidateUsersAsync([user.Id], CancellationToken.None);

        return Result.Success();
    }
}
