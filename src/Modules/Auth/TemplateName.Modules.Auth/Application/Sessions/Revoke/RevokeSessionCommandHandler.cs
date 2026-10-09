using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Sessions.Revoke;

/// <summary>
/// Revokes one of the caller's sessions and with it every refresh token of the session, so it cannot refresh any more (its access token
/// works until it expires, decision D9). A session that does not exist and a session of another user answer with the same
/// <see cref="SessionErrors.NotFound"/>, so ids cannot be probed. A session of the caller that has already ended succeeds without
/// changing anything: revoking twice is not an error, as with logout. Revoking the current session is allowed.
/// </summary>
internal sealed class RevokeSessionCommandHandler(
    ISessionRepository sessions,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<RevokeSessionCommand>
{
    public async Task<Result> HandleAsync(RevokeSessionCommand command, CancellationToken cancellationToken)
    {
        var session = await sessions.GetByIdAsync(command.SessionId, cancellationToken);
        if (session is null || currentUser.UserId is not { } userId || session.UserId != userId)
        {
            return Result.Failure(SessionErrors.NotFound(command.SessionId));
        }

        if (session.RevokedAt is not null)
        {
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();
        session.Revoke(SessionRevokedReason.Logout, now);
        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.SessionRevoked, succeeded: true, now, userId: userId, sessionId: session.Id));

        // A revocation is stored even when the client goes away; the session has no concurrency token, so the save cannot conflict.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Success();
    }
}
