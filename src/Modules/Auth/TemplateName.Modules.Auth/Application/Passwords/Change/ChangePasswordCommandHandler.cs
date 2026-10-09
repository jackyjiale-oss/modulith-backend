using System.Text.Json;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Passwords.Change;

/// <summary>
/// Replaces the signed-in user's password after verifying the current one, then the breach and reuse checks. Every other session of the
/// user is revoked; the current one (the access token's <c>sid</c>) takes the new security stamp and stays signed in (spec 12.2). A wrong
/// current password is audited but not counted towards the login lockout. A successful change invalidates every pending reset link of
/// the user. A caller whose account is gone or suspended gets
/// <see cref="UserErrors.NotFound"/>, which the endpoint answers like a request without a valid token.
/// </summary>
internal sealed class ChangePasswordCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IVerificationCodeRepository verificationCodes,
    IPasswordHasher passwordHasher,
    IBreachedPasswordChecker breachedPasswordChecker,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    IOptions<PasswordOptions> passwordOptions,
    TimeProvider timeProvider) : ICommandHandler<ChangePasswordCommand>
{
    public async Task<Result> HandleAsync(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Result.Failure(UserErrors.NotFound(Guid.Empty));
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null || user.Status == UserStatus.Suspended)
        {
            return Result.Failure(UserErrors.NotFound(userId));
        }

        var now = timeProvider.GetUtcNow();
        if (!VerifyCurrentPassword(user, command.CurrentPassword))
        {
            return await FailAsync(user, UserErrors.CurrentPasswordIncorrect, now, cancellationToken);
        }

        if (await breachedPasswordChecker.IsBreachedAsync(command.NewPassword, cancellationToken))
        {
            return await FailAsync(user, UserErrors.PasswordBreached, now, cancellationToken);
        }

        if (passwordHasher.IsRecentPassword(user, command.NewPassword))
        {
            return await FailAsync(user, UserErrors.PasswordReused, now, cancellationToken);
        }

        user.ChangePassword(passwordHasher.Hash(command.NewPassword), passwordOptions.Value.HistoryCount, now);

        // A reset link emailed earlier (or leaked) must not outlive the password it was meant to replace.
        foreach (var pending in await verificationCodes.GetPendingAsync(user.Id, VerificationPurpose.PasswordReset, now, cancellationToken))
        {
            pending.Invalidate(now);
        }

        // The new stamp ends every session at its next refresh; the current one adopts it and stays, the others are revoked now.
        var revokedSessionCount = 0;
        foreach (var session in await sessions.GetActiveByUserAsync(user.Id, now, cancellationToken))
        {
            if (session.Id == currentUser.SessionId)
            {
                session.AdoptSecurityStamp(user.SecurityStamp);
                continue;
            }

            session.Revoke(SessionRevokedReason.PasswordChanged, now);
            revokedSessionCount++;
        }

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.PasswordChanged,
            succeeded: true,
            now,
            userId: user.Id,
            sessionId: currentUser.SessionId,
            details: JsonSerializer.Serialize(new { revokedSessionCount })));

        // One save: the password, the stamp, the history, the sessions, the invalidated reset codes, the audit entry and the outbox row
        // of PasswordChangedDomainEvent.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>Exactly one verification: the stored hash, or the dummy for an account without a password, which never matches.</summary>
    private bool VerifyCurrentPassword(User user, string currentPassword)
    {
        if (user.PasswordHash is { } passwordHash)
        {
            return passwordHasher.Verify(passwordHash, currentPassword) != PasswordVerification.Failed;
        }

        passwordHasher.SpendVerificationCost(currentPassword);
        return false;
    }

    private async Task<Result> FailAsync(User user, Error error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.PasswordChangeFailed,
            succeeded: false,
            now,
            userId: user.Id,
            failureReason: error.Code,
            sessionId: currentUser.SessionId));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Failure(error);
    }
}
