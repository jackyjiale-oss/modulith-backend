using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users.RevokeSessions;

/// <summary>
/// Revokes every active session of a user with <see cref="SessionRevokedReason.AdminRevoked"/>, so none of their refresh tokens works
/// any more (access tokens already issued work until they expire, decision D9). The account itself is untouched: the user can sign in
/// again. Audited, also when there was nothing to revoke. Only a SuperAdmin can do this to a SuperAdmin.
/// </summary>
internal sealed class RevokeUserSessionsCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    SuperAdminRules superAdminRules,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<RevokeUserSessionsCommand>
{
    public async Task<Result> HandleAsync(RevokeUserSessionsCommand command, CancellationToken cancellationToken)
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
        foreach (var session in await sessions.GetActiveByUserAsync(user.Id, now, cancellationToken))
        {
            session.Revoke(SessionRevokedReason.AdminRevoked, now);
        }

        auditWriter.Record(AdminAudit.Succeeded(AuthAuditEvents.AdminSessionsRevoked, user.Id, command.ActorId, now));

        // Revocations are stored even when the client goes away; sessions have no concurrency token, so the save cannot conflict.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Success();
    }
}
