using System.Text.Json;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Passwords.Reset;

/// <summary>
/// Sets a new password with a password-reset code. The token proves control of the mailbox, so the reset also confirms the email address
/// (this is how an account an administrator created gets its first password) and ends any lockout; every session of the user is revoked.
/// An unknown, used, replaced, expired or wrong-purpose token, and one whose account is gone or suspended, all give
/// <see cref="VerificationErrors.InvalidToken"/>. The code is consumed by one conditional statement in the database, so of two
/// simultaneous resets with one token exactly one succeeds. The breach check runs before that (it depends on the password alone); the
/// reuse check runs after it, so a reused password uses up the link and the link can never be a free oracle for the account's passwords.
/// A successful reset also invalidates every other pending reset link of the user.
/// </summary>
internal sealed class ResetPasswordCommandHandler(
    IUserRepository users,
    IVerificationCodeRepository verificationCodes,
    ISessionRepository sessions,
    IPasswordHasher passwordHasher,
    IBreachedPasswordChecker breachedPasswordChecker,
    ISecureTokenService tokenService,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    IOptions<PasswordOptions> passwordOptions,
    TimeProvider timeProvider) : ICommandHandler<ResetPasswordCommand>
{
    public async Task<Result> HandleAsync(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var code = await verificationCodes.GetByTokenHashAsync(tokenService.Hash(command.Token), cancellationToken);
        if (code is null || !code.CanConsume(VerificationPurpose.PasswordReset, now))
        {
            return await FailAsync(VerificationErrors.InvalidToken, code?.UserId, now, cancellationToken);
        }

        // A soft-deleted user is not found. A suspended one is not reactivated by a reset and gets the same answer as a bad token.
        var user = await users.GetByIdAsync(code.UserId, cancellationToken);
        if (user is null || user.Status == UserStatus.Suspended)
        {
            return await FailAsync(VerificationErrors.InvalidToken, code.UserId, now, cancellationToken);
        }

        if (await breachedPasswordChecker.IsBreachedAsync(command.NewPassword, cancellationToken))
        {
            return await FailAsync(UserErrors.PasswordBreached, user.Id, now, cancellationToken);
        }

        // The database decides which of two simultaneous resets with this token wins; the loser answers like a used token. The claim
        // commits on its own, so from here on nothing is cancelled with the request.
        if (!await verificationCodes.TryConsumeAsync(code.Id, VerificationPurpose.PasswordReset, now, CancellationToken.None))
        {
            return await FailAsync(VerificationErrors.InvalidToken, user.Id, now, cancellationToken);
        }

        code.Consume(VerificationPurpose.PasswordReset, now);

        // Only after the link is used up: the history holds the current hash, so checking before the claim would let whoever holds the
        // link test candidate passwords for free and silently (the owner sees nothing until the link is used). A reused password now
        // costs the link, and the owner asks for a new one.
        if (passwordHasher.IsRecentPassword(user, command.NewPassword))
        {
            return await FailAsync(UserErrors.PasswordReused, user.Id, now, CancellationToken.None);
        }

        user.ChangePassword(passwordHasher.Hash(command.NewPassword), passwordOptions.Value.HistoryCount, now);
        user.ConfirmEmail(now);

        // Every other reset link (a second forgot, or a leaked one) dies with this reset. The one just used is already consumed.
        foreach (var pending in await verificationCodes.GetPendingAsync(user.Id, VerificationPurpose.PasswordReset, now, CancellationToken.None))
        {
            pending.Invalidate(now);
        }

        var activeSessions = await sessions.GetActiveByUserAsync(user.Id, now, CancellationToken.None);
        foreach (var session in activeSessions)
        {
            session.Revoke(SessionRevokedReason.PasswordChanged, now);
        }

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.PasswordReset,
            succeeded: true,
            now,
            userId: user.Id,
            details: JsonSerializer.Serialize(new { revokedSessionCount = activeSessions.Count })));

        // One save: the password, the confirmation, the consumed and invalidated codes, the revocations, the audit entry and the outbox
        // rows.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Success();
    }

    private async Task<Result> FailAsync(Error error, Guid? userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.PasswordResetFailed,
            succeeded: false,
            now,
            userId: userId,
            failureReason: error.Code));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Failure(error);
    }
}
