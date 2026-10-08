using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users.Unlock;

/// <summary>
/// Unlocks an account: <see cref="User.Reinstate"/> makes it active again and also ends a login lockout (failed sign-ins), so the user
/// can sign in at once. The sessions revoked by the lock stay revoked: the user signs in again. Audited, saved, and the user's cached
/// permissions removed. Only a SuperAdmin can unlock a SuperAdmin. Unlocking an active account succeeds (and clears its failures).
/// </summary>
internal sealed class UnlockUserCommandHandler(
    IUserRepository users,
    SuperAdminRules superAdminRules,
    IPermissionCache permissionCache,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<UnlockUserCommand>
{
    public async Task<Result> HandleAsync(UnlockUserCommand command, CancellationToken cancellationToken)
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

        var now = timeProvider.GetUtcNow();
        user.Reinstate(now);
        auditWriter.Record(AdminAudit.Succeeded(AuthAuditEvents.AdminUserUnlocked, user.Id, command.ActorId, now));

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await permissionCache.InvalidateUsersAsync([user.Id], CancellationToken.None);

        return Result.Success();
    }
}
