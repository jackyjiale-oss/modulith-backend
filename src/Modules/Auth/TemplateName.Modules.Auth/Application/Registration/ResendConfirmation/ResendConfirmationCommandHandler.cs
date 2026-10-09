using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Registration.ResendConfirmation;

/// <summary>
/// Issues a new email-confirmation code and invalidates the pending ones. An unknown or already confirmed address, or a code issued
/// within <see cref="VerificationOptions.ResendCooldown"/>, does nothing; every case succeeds, so the answer reveals nothing.
/// </summary>
internal sealed class ResendConfirmationCommandHandler(
    IUserRepository users,
    IVerificationCodeRepository verificationCodes,
    ISecureTokenService tokenService,
    ISecretProtector secretProtector,
    IAuthAuditWriter auditWriter,
    IClientContext clientContext,
    IUnitOfWork unitOfWork,
    IOptions<VerificationOptions> verificationOptions,
    TimeProvider timeProvider) : ICommandHandler<ResendConfirmationCommand>
{
    public async Task<Result> HandleAsync(ResendConfirmationCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByNormalizedEmailAsync(User.NormalizeEmail(command.Email), cancellationToken);
        if (user is null || user.EmailConfirmed)
        {
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();
        var options = verificationOptions.Value;

        // The cooldown is enforced here, per account, from the last code's CreatedAt (review 2.11): the limiter cannot see the body.
        var lastIssuedAt = await verificationCodes.GetLastIssuedAtAsync(user.Id, VerificationPurpose.EmailVerify, cancellationToken);
        if (lastIssuedAt is { } issuedAt && now.UtcDateTime - issuedAt < options.ResendCooldown)
        {
            return Result.Success();
        }

        foreach (var pending in await verificationCodes.GetPendingAsync(user.Id, VerificationPurpose.EmailVerify, now, cancellationToken))
        {
            pending.Invalidate(now);
        }

        var token = tokenService.Generate();
        verificationCodes.Add(VerificationCode.Issue(
            user.Id,
            VerificationPurpose.EmailVerify,
            VerificationTrigger.SelfService,
            user.NormalizedEmail,
            token.Hash,
            secretProtector.Protect(token.Value),
            options.EmailLifetime,
            clientContext.IpAddress,
            now));
        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.ConfirmationResent, succeeded: true, now, userId: user.Id));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
