using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Authentication.Logout;

/// <summary>
/// Revokes the caller's current session (<see cref="ICurrentUser.SessionId"/>) and with it every refresh token of the session. It always
/// succeeds: a token without a session, a session that is unknown, already revoked or not the caller's leaves everything as it is. The
/// access token itself keeps working until it expires (decision D9).
/// </summary>
internal sealed class LogoutCommandHandler(
    ISessionRepository sessions,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<LogoutCommand>
{
    public async Task<Result> HandleAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.SessionId is not { } sessionId)
        {
            return Result.Success();
        }

        var session = await sessions.GetByIdAsync(sessionId, cancellationToken);
        if (session is null || session.UserId != currentUser.UserId || session.RevokedAt is not null)
        {
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();
        session.Revoke(SessionRevokedReason.Logout, now);
        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.Logout, succeeded: true, now, userId: session.UserId, sessionId: session.Id));

        // A revocation is stored even when the client goes away; the session has no concurrency token, so the save cannot conflict.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Success();
    }
}
