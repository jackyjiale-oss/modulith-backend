using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Passwords.Forgot;

/// <summary>
/// Issues a password-reset code, whose event emails the link, and invalidates the pending ones. Every case succeeds, so the answer
/// reveals nothing: an unknown address, a suspended account, or a code issued within <see cref="VerificationOptions.ResendCooldown"/>
/// issues nothing. Every case also writes one audit entry (with the masked address) and saves once, so a known and an unknown address do
/// comparable work. An account without a password (created by an administrator) is a known account here: the reset is how it sets its
/// first password (Ruling R3). The link itself is issued by <see cref="PasswordResetLinkIssuer"/>, the same mechanism the user
/// administration uses.
/// </summary>
internal sealed class ForgotPasswordCommandHandler(
    IUserRepository users,
    IVerificationCodeRepository verificationCodes,
    PasswordResetLinkIssuer linkIssuer,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    IOptions<VerificationOptions> verificationOptions,
    TimeProvider timeProvider) : ICommandHandler<ForgotPasswordCommand>
{
    /// <summary>The audit failure reason of an address no account has.</summary>
    internal const string UnknownEmailReason = "unknown_email";

    /// <summary>The audit failure reason of a request inside the cooldown.</summary>
    internal const string CooldownReason = "cooldown";

    public async Task<Result> HandleAsync(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var attemptedIdentifier = AuthAuditLog.MaskIdentifier(command.Email);
        var user = await users.GetByNormalizedEmailAsync(User.NormalizeEmail(command.Email), cancellationToken);

        if (user is null)
        {
            return await IgnoreAsync(userId: null, UnknownEmailReason, attemptedIdentifier, now, cancellationToken);
        }

        // A reset would not let a suspended account sign in, and the reset handler refuses it; send nothing.
        if (user.Status == UserStatus.Suspended)
        {
            return await IgnoreAsync(user.Id, UserErrors.AccountInactive.Code, attemptedIdentifier, now, cancellationToken);
        }

        var options = verificationOptions.Value;

        // The cooldown is enforced here, per account, from the last reset code's CreatedAt (review 2.11): the limiter cannot see the body.
        var lastIssuedAt = await verificationCodes.GetLastIssuedAtAsync(user.Id, VerificationPurpose.PasswordReset, cancellationToken);
        if (lastIssuedAt is { } issuedAt && now.UtcDateTime - issuedAt < options.ResendCooldown)
        {
            return await IgnoreAsync(user.Id, CooldownReason, attemptedIdentifier, now, cancellationToken);
        }

        await linkIssuer.IssueAsync(user, VerificationTrigger.SelfService, now, cancellationToken);
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.PasswordForgotRequested,
            succeeded: true,
            now,
            userId: user.Id,
            attemptedIdentifier: attemptedIdentifier));

        // One save: the new code, the invalidated ones, the audit entry and the outbox row of the issued event commit together.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    private async Task<Result> IgnoreAsync(
        Guid? userId,
        string reason,
        string? attemptedIdentifier,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.PasswordForgotRequested,
            succeeded: false,
            now,
            userId: userId,
            failureReason: reason,
            attemptedIdentifier: attemptedIdentifier));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
