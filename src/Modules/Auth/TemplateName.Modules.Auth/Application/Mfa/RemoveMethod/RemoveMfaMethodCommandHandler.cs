using System.Text.Json;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Mfa.RemoveMethod;

/// <summary>
/// Removes one of the caller's verification methods (plan decisions D11, D13 and D18). The order is fixed:
/// <list type="number">
/// <item>The caller's user and session, as at step-up; otherwise <see cref="UserErrors.NotFound"/> (answered like a request without a
/// valid token).</item>
/// <item>The method, looked up <b>scoped to the caller</b>: an unknown id and another user's id are the same
/// <see cref="MfaErrors.MethodNotFound"/>, whatever the caller has proved, so nothing tells that a method exists.</item>
/// <item>A <b>pending</b> method (a setup never confirmed) protects nothing: it is removed and nothing else changes, with no step-up, no
/// new stamp, no revoked session and no audit row.</item>
/// <item>A <b>confirmed</b> method needs a fresh step-up proved with a code (<see cref="StepUpPolicy.Check"/> with a confirmed method:
/// a password is not enough), otherwise <see cref="MfaErrors.StepUpRequired"/>. Then the method and every recovery code are removed,
/// <see cref="MfaChangeEffects"/> rotates the stamp, revokes every other session (<see cref="SessionRevokedReason.MfaChanged"/>) and
/// keeps this one, and <c>auth.mfa_disabled</c> is audited with the revoked count.</item>
/// </list>
/// One save commits all of it. The method is removed through the tracked entity, so its concurrency tokens (the secret and the
/// confirmation as loaded) apply to the DELETE: a removal that loaded a pending method which a simultaneous confirm turned on, or whose
/// method a simultaneous removal already deleted, fails with 409 <c>concurrency.conflict</c> and deletes nothing. The user's
/// <c>RowVersion</c> serializes two removals of a confirmed method the same way.
/// </summary>
internal sealed class RemoveMfaMethodCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IMfaMethodRepository mfaMethods,
    IRecoveryCodeRepository recoveryCodes,
    MfaChangeEffects mfaChangeEffects,
    StepUpPolicy stepUpPolicy,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<RemoveMfaMethodCommand>
{
    public async Task<Result> HandleAsync(RemoveMfaMethodCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var caller = await CallerSession.ResolveAsync(currentUser, users, sessions, now, cancellationToken);
        if (caller.IsFailure)
        {
            return Result.Failure(caller.Error);
        }

        var (user, session) = caller.Value;
        var method = await mfaMethods.GetForUserAsync(command.MethodId, user.Id, cancellationToken);
        if (method is null)
        {
            return Result.Failure(MfaErrors.MethodNotFound(command.MethodId));
        }

        if (!method.IsConfirmed)
        {
            // Cancelling a setup nobody finished changes nothing else about the account.
            mfaMethods.Remove(method);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        var stepUp = stepUpPolicy.Check(hasConfirmedMfa: true);
        if (stepUp.IsFailure)
        {
            return stepUp;
        }

        mfaMethods.Remove(method);
        foreach (var code in await recoveryCodes.ListByUserAsync(user.Id, cancellationToken))
        {
            recoveryCodes.Remove(code);
        }

        var revokedSessionCount = await mfaChangeEffects.ApplyAsync(
            user,
            MfaChange.Disabled,
            session.Id,
            SessionRevokedReason.MfaChanged,
            now,
            cancellationToken);

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.MfaDisabled,
            succeeded: true,
            now,
            userId: user.Id,
            sessionId: session.Id,
            details: JsonSerializer.Serialize(new { revokedSessionCount })));

        // One save: the method, the codes, the stamp, the sessions, the audit row and the outbox row of MfaChangedDomainEvent.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
