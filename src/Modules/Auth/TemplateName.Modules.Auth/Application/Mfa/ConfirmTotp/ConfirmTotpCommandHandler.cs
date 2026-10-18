using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Mfa.ConfirmTotp;

/// <summary>
/// Confirms a pending authenticator setup with a code from the app and turns two-step verification on (plan decisions D3, D5, D11 and
/// D13). The order is fixed:
/// <list type="number">
/// <item>A fresh step-up (<see cref="StepUpPolicy.Check"/> with no confirmed method: the account has none until this succeeds),
/// otherwise <see cref="MfaErrors.StepUpRequired"/>.</item>
/// <item>The caller's user and session, as at step-up; otherwise <see cref="UserErrors.NotFound"/> (answered like a request without a
/// valid token).</item>
/// <item>No pending method (none, or one already confirmed), or a pending secret that can no longer be read (a key-ring loss since the
/// setup; logged with the method id), is <see cref="MfaErrors.SetupNotPending"/>: the user starts the setup again.</item>
/// <item>A code that matches no step around now is <see cref="MfaErrors.InvalidCode"/>, and is <b>not</b> counted toward the lockout:
/// the caller created this secret a moment ago and has just proved a fresh identity, so a wrong code guesses nothing of value.</item>
/// <item>The method is confirmed with the matched step as its last used step, so the same code cannot step up or sign in afterwards; the
/// whole set of recovery codes is replaced; <see cref="MfaChangeEffects"/> rotates the stamp, revokes every other session
/// (<see cref="SessionRevokedReason.MfaChanged"/>) and keeps this one; audit <c>auth.mfa_enabled</c> with the revoked count.</item>
/// </list>
/// One save commits all of it. It writes the user row, whose <c>RowVersion</c> makes a simultaneous second confirm fail with 409
/// <c>concurrency.conflict</c> instead of issuing a second set of codes, and the method row, whose concurrency tokens (the secret and
/// the confirmation as loaded) make it fail the same way when a setup restart replaced the secret meanwhile, so a secret nobody
/// verified is never confirmed. The recovery codes leave only in the answer.
/// </summary>
internal sealed partial class ConfirmTotpCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IMfaMethodRepository mfaMethods,
    IMfaSecretProtector secretProtector,
    RecoveryCodeIssuer recoveryCodeIssuer,
    MfaChangeEffects mfaChangeEffects,
    StepUpPolicy stepUpPolicy,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    ILogger<ConfirmTotpCommandHandler> logger,
    TimeProvider timeProvider) : ICommandHandler<ConfirmTotpCommand, RecoveryCodesResponse>
{
    public async Task<Result<RecoveryCodesResponse>> HandleAsync(ConfirmTotpCommand command, CancellationToken cancellationToken)
    {
        var stepUp = stepUpPolicy.Check(hasConfirmedMfa: false);
        if (stepUp.IsFailure)
        {
            return Result.Failure<RecoveryCodesResponse>(stepUp.Error);
        }

        var now = timeProvider.GetUtcNow();
        if (currentUser.UserId is not { } userId || currentUser.SessionId is not { } sessionId)
        {
            return Result.Failure<RecoveryCodesResponse>(UserErrors.NotFound(currentUser.UserId ?? Guid.Empty));
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null || user.Status == UserStatus.Suspended)
        {
            return Result.Failure<RecoveryCodesResponse>(UserErrors.NotFound(userId));
        }

        // The kept session adopts the new stamp below, so it must be the caller's live session: never revive one that has ended.
        var session = await sessions.GetByIdAsync(sessionId, cancellationToken);
        if (session is null
            || session.UserId != userId
            || !session.IsActive(now)
            || !string.Equals(session.SecurityStamp, user.SecurityStamp, StringComparison.Ordinal))
        {
            return Result.Failure<RecoveryCodesResponse>(UserErrors.NotFound(userId));
        }

        var method = await mfaMethods.GetTotpAsync(userId, cancellationToken);
        if (method is null || method.IsConfirmed)
        {
            return Result.Failure<RecoveryCodesResponse>(MfaErrors.SetupNotPending);
        }

        if (ReadSecret(method) is not { } secret)
        {
            return Result.Failure<RecoveryCodesResponse>(MfaErrors.SetupNotPending);
        }

        long? matched;
        try
        {
            // The method is pending, so no step has been used yet.
            matched = Totp.FindMatchingStep(secret, command.Code, now, lastUsedTimeStep: null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        if (matched is not { } step)
        {
            return Result.Failure<RecoveryCodesResponse>(MfaErrors.InvalidCode);
        }

        var confirmed = method.Confirm(step, now);
        if (confirmed.IsFailure)
        {
            return Result.Failure<RecoveryCodesResponse>(confirmed.Error);
        }

        var recoveryCodes = await recoveryCodeIssuer.ReplaceAsync(userId, now, cancellationToken);
        var revokedSessionCount = await mfaChangeEffects.ApplyAsync(
            user,
            MfaChange.Enabled,
            session.Id,
            SessionRevokedReason.MfaChanged,
            now,
            cancellationToken);

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.MfaEnabled,
            succeeded: true,
            now,
            userId: userId,
            sessionId: session.Id,
            details: JsonSerializer.Serialize(new { revokedSessionCount })));

        // One save: the method, the old and new recovery codes, the stamp, the sessions, the audit row and the outbox row of
        // MfaChangedDomainEvent. A simultaneous confirm that saved first changed the user's RowVersion, so this one throws (409).
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new RecoveryCodesResponse(recoveryCodes);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read the TOTP secret of MFA method {MethodId}")]
    private static partial void LogUnreadableSecret(ILogger logger, Guid methodId);

    /// <summary>The plain secret of the pending method, or null (logged with the method id only) when it cannot be read.</summary>
    private byte[]? ReadSecret(UserMfaMethod method)
    {
        try
        {
            return secretProtector.Unprotect(method.SecretEncrypted);
        }
        catch (CryptographicException)
        {
            LogUnreadableSecret(logger, method.Id);
            return null;
        }
    }
}
