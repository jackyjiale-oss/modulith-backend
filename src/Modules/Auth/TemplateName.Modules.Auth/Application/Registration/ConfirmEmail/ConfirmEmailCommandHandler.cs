using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Registration.ConfirmEmail;

/// <summary>
/// Consumes an email-confirmation code and confirms its user's address. An unknown, used, replaced, expired or wrong-purpose token,
/// or one whose user is gone, all give <see cref="VerificationErrors.InvalidToken"/>; the failure is audited, never with the token. The
/// code is consumed by one conditional statement in the database, so of two simultaneous confirmations with one token exactly one
/// succeeds and the other gets the same invalid-token answer.
/// </summary>
internal sealed class ConfirmEmailCommandHandler(
    IUserRepository users,
    IVerificationCodeRepository verificationCodes,
    ISecureTokenService tokenService,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<ConfirmEmailCommand>
{
    public async Task<Result> HandleAsync(ConfirmEmailCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var code = await verificationCodes.GetByTokenHashAsync(tokenService.Hash(command.Token), cancellationToken);

        if (code is null || !code.CanConsume(VerificationPurpose.EmailVerify, now))
        {
            return await FailAsync(code?.UserId, now, cancellationToken);
        }

        var user = await users.GetByIdAsync(code.UserId, cancellationToken);
        if (user is null)
        {
            return await FailAsync(code.UserId, now, cancellationToken);
        }

        // The database decides which of two simultaneous confirmations wins; the loser answers like a used token. The claim commits on
        // its own, so from here on nothing is cancelled with the request.
        if (!await verificationCodes.TryConsumeAsync(code.Id, VerificationPurpose.EmailVerify, now, CancellationToken.None))
        {
            return await FailAsync(code.UserId, now, cancellationToken);
        }

        code.Consume(VerificationPurpose.EmailVerify, now);
        user.ConfirmEmail(now);
        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.EmailConfirmed, succeeded: true, now, userId: user.Id));
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Success();
    }

    private async Task<Result> FailAsync(Guid? userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.EmailConfirmFailed,
            succeeded: false,
            now,
            userId: userId,
            failureReason: VerificationErrors.InvalidToken.Code));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Failure(VerificationErrors.InvalidToken);
    }
}
