using System.Text.Json;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Authentication.LogoutAll;

/// <summary>
/// Revokes every active session of the caller (<see cref="ISessionRepository.GetActiveByUserAsync"/>) with
/// <see cref="SessionRevokedReason.LogoutAll"/>, so none of their refresh tokens works any more; access tokens already issued keep
/// working until they expire (decision D9). One audit entry records how many sessions ended.
/// </summary>
internal sealed class LogoutAllCommandHandler(
    ISessionRepository sessions,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<LogoutAllCommand>
{
    public async Task<Result> HandleAsync(LogoutAllCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();
        var activeSessions = await sessions.GetActiveByUserAsync(userId, now, cancellationToken);
        foreach (var session in activeSessions)
        {
            session.Revoke(SessionRevokedReason.LogoutAll, now);
        }

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.LogoutAll,
            succeeded: true,
            now,
            userId: userId,
            sessionId: currentUser.SessionId,
            details: JsonSerializer.Serialize(new { sessionCount = activeSessions.Count })));

        // Revocations are stored even when the client goes away; sessions have no concurrency token, so the save cannot conflict.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Success();
    }
}
