using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Registration.ConfirmEmail;

/// <summary>
/// Consumes an email-confirmation code and confirms its user's address. An unknown, used, replaced, expired or wrong-purpose token,
/// or one whose user is gone, all give <see cref="VerificationErrors.InvalidToken"/>; the failure is audited, never with the token.
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

        // A refused Consume changes nothing on the code.
        if (code is null || code.Consume(VerificationPurpose.EmailVerify, now).IsFailure)
        {
            return await FailAsync(code?.UserId, now, cancellationToken);
        }

        var user = await users.GetByIdAsync(code.UserId, cancellationToken);
        if (user is null)
        {
            return await FailAsync(code.UserId, now, cancellationToken);
        }

        user.ConfirmEmail(now);
        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.EmailConfirmed, succeeded: true, now, userId: user.Id));
        await unitOfWork.SaveChangesAsync(cancellationToken);

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
