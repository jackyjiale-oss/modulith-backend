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

namespace TemplateName.Modules.Auth.Application.Mfa.RegenerateRecoveryCodes;

/// <summary>
/// Replaces the caller's whole set of recovery codes (plan decisions D5, D11 and D13). The order is fixed:
/// <list type="number">
/// <item>The caller's user and session, as at step-up; otherwise <see cref="UserErrors.NotFound"/> (answered like a request without a
/// valid token).</item>
/// <item>No confirmed method is <see cref="MfaErrors.NotEnabled"/>, answered before the step-up: an account without two-step
/// verification could never satisfy it, so the useful answer is that there is nothing to regenerate.</item>
/// <item>A fresh step-up proved with a code (<see cref="StepUpPolicy.Check"/> with a confirmed method), otherwise
/// <see cref="MfaErrors.StepUpRequired"/>.</item>
/// <item>Every old code, used or not, is replaced (<see cref="RecoveryCodeIssuer"/>); <see cref="MfaChangeEffects"/> rotates the stamp,
/// revokes every other session (<see cref="SessionRevokedReason.MfaChanged"/>) and keeps this one; audit
/// <c>auth.recovery_codes_regenerated</c> with the revoked count only.</item>
/// </list>
/// One save commits all of it; it writes the user row, whose <c>RowVersion</c> makes a simultaneous second regeneration fail with 409
/// <c>concurrency.conflict</c> instead of leaving two sets. The new codes leave only in the answer.
/// </summary>
internal sealed class RegenerateRecoveryCodesCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IMfaMethodRepository mfaMethods,
    RecoveryCodeIssuer recoveryCodeIssuer,
    MfaChangeEffects mfaChangeEffects,
    StepUpPolicy stepUpPolicy,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<RegenerateRecoveryCodesCommand, RecoveryCodesResponse>
{
    public async Task<Result<RecoveryCodesResponse>> HandleAsync(RegenerateRecoveryCodesCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var caller = await CallerSession.ResolveAsync(currentUser, users, sessions, now, cancellationToken);
        if (caller.IsFailure)
        {
            return Result.Failure<RecoveryCodesResponse>(caller.Error);
        }

        var (user, session) = caller.Value;
        if (!await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken))
        {
            return Result.Failure<RecoveryCodesResponse>(MfaErrors.NotEnabled);
        }

        var stepUp = stepUpPolicy.Check(hasConfirmedMfa: true);
        if (stepUp.IsFailure)
        {
            return Result.Failure<RecoveryCodesResponse>(stepUp.Error);
        }

        var recoveryCodes = await recoveryCodeIssuer.ReplaceAsync(user.Id, now, cancellationToken);
        var revokedSessionCount = await mfaChangeEffects.ApplyAsync(
            user,
            MfaChange.RecoveryCodesRegenerated,
            session.Id,
            SessionRevokedReason.MfaChanged,
            now,
            cancellationToken);

        // The revoked count only: a code never reaches an audit row.
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.RecoveryCodesRegenerated,
            succeeded: true,
            now,
            userId: user.Id,
            sessionId: session.Id,
            details: JsonSerializer.Serialize(new { revokedSessionCount })));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new RecoveryCodesResponse(recoveryCodes);
    }
}
