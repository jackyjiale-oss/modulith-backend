using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.PhoneSignIn.Send;

/// <summary>
/// Texts a sign-in code to the account whose verified number was given (spec 5.2). Every well-formed request succeeds the same way, so
/// the answer never tells whether the number has an account: an unknown number, an account that may not sign in, the SMS budget and a
/// lost race all text nothing. Only <see cref="PhoneErrors.InvalidPhoneNumber"/>, which depends on the request alone, is a failure.
/// </summary>
internal sealed class SendPhoneSignInCodeCommandHandler(
    IUserRepository users,
    IPhoneVerificationCodeRepository codes,
    PhoneSignInGate gate,
    PhoneCodeBudget budget,
    ISecretProtector secretProtector,
    IAuthAuditWriter auditWriter,
    IAuthMetrics metrics,
    IUnitOfWork unitOfWork,
    IOptions<PhoneOptions> options,
    TimeProvider timeProvider) : ICommandHandler<SendPhoneSignInCodeCommand>
{
    public async Task<Result> HandleAsync(SendPhoneSignInCodeCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var settings = options.Value;
        if (!PhoneNumberRules.TryNormalize(command.PhoneNumber, settings.AllowedRegionSet(), out var number))
        {
            return Result.Failure(PhoneErrors.InvalidPhoneNumber);
        }

        // Nothing about an unknown number is written: no row, no audit, no log line.
        var user = await users.GetByPhoneNumberAsync(number, cancellationToken);
        if (user is null)
        {
            metrics.RecordPhoneSignIn(PhoneSignInOutcome.Suppressed);
            return Result.Success();
        }

        if (await RefusalAsync(user, settings, now, cancellationToken) is { } reason)
        {
            auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.PhoneSignInRequested, succeeded: false, now, userId: user.Id, failureReason: reason));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            metrics.RecordPhoneSignIn(PhoneSignInOutcome.Suppressed);
            return Result.Success();
        }

        foreach (var pending in await codes.ListPendingAsync(user.Id, PhoneCodePurpose.SignIn, now, cancellationToken))
        {
            pending.Invalidate(now);
        }

        var plainCode = PhoneVerificationCode.NewCode();
        codes.Add(PhoneVerificationCode.Issue(
            user.Id,
            PhoneCodePurpose.SignIn,
            number,
            plainCode,
            secretProtector.Protect(plainCode),
            settings.SignInCodeLifetime,
            now));
        user.NotePhoneCodeRequested(now);
        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.PhoneSignInRequested, succeeded: true, now, userId: user.Id));

        // One save: the ended codes, the new code, its outbox row, the audit row and the user's request time, whose RowVersion makes a
        // simultaneous send lose. The loser texts nothing and still succeeds: a 409 would say the number has an account.
        var saved = await unitOfWork.TrySaveChangesAsync(cancellationToken);
        metrics.RecordPhoneSignIn(saved ? PhoneSignInOutcome.Sent : PhoneSignInOutcome.Suppressed);
        return Result.Success();
    }

    private async Task<string?> RefusalAsync(User user, PhoneOptions settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (user.Status == UserStatus.Suspended)
        {
            return "inactive";
        }

        if (user.IsLockedOut(now))
        {
            return "locked";
        }

        if (await gate.IsDeniedAsync(user, cancellationToken))
        {
            return "role_denied";
        }

        return (await budget.CheckAsync(user.Id, settings, now, cancellationToken))?.Reason;
    }
}
