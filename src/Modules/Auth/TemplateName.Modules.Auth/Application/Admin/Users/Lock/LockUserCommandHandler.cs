using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users.Lock;

/// <summary>
/// Locks an account: <see cref="User.Suspend"/> (the security stamp changes, so no session can refresh with the old one), every active
/// session revoked with <see cref="SessionRevokedReason.AdminRevoked"/>, an audit entry, one save, then the user's cached permissions
/// removed so the suspension also applies to their next permission check on this instance (access tokens already issued still pass
/// the bearer check until they expire, decision D9; the suspended user has no permissions). A caller cannot lock themselves, only a
/// SuperAdmin can lock a SuperAdmin, and the last active SuperAdmin cannot be locked. Locking a suspended account succeeds again.
/// </summary>
internal sealed class LockUserCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    SuperAdminRules superAdminRules,
    IPermissionCache permissionCache,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<LockUserCommand>
{
    public async Task<Result> HandleAsync(LockUserCommand command, CancellationToken cancellationToken)
    {
        if (command.UserId == command.ActorId)
        {
            return Result.Failure(UserErrors.CannotLockSelf);
        }

        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserErrors.NotFound(command.UserId));
        }

        var scope = await superAdminRules.GetScopeAsync(command.ActorId, cancellationToken);
        var allowed = scope.EnsureCanManage(user);
        if (allowed.IsSuccess)
        {
            allowed = await superAdminRules.EnsureNotLastActiveAsync(scope, user, cancellationToken);
        }

        if (allowed.IsFailure)
        {
            return allowed;
        }

        var now = timeProvider.GetUtcNow();
        user.Suspend(now);
        foreach (var session in await sessions.GetActiveByUserAsync(user.Id, now, cancellationToken))
        {
            session.Revoke(SessionRevokedReason.AdminRevoked, now);
        }

        auditWriter.Record(AdminAudit.Succeeded(AuthAuditEvents.AdminUserLocked, user.Id, command.ActorId, now));

        // A revocation is stored even when the client goes away (as for logout-all); the user's RowVersion still guards the row.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        await permissionCache.InvalidateUsersAsync([user.Id], CancellationToken.None);

        return Result.Success();
    }
}
