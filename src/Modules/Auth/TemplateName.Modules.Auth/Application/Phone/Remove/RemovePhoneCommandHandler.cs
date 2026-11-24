using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Phone.Remove;

/// <summary>
/// Removes the caller's verified number. The caller's user and live session first (<see cref="CallerSession"/>); a verified number
/// needs a fresh step-up (<see cref="StepUpPolicy"/>, proved with a code when the account has two-step verification), and its removal
/// is audited as <c>auth.phone_number_removed</c>. Every pending code ends either way, and the answer is a success also when there was
/// no number. One save.
/// </summary>
internal sealed class RemovePhoneCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IPhoneVerificationCodeRepository codes,
    IMfaMethodRepository mfaMethods,
    StepUpPolicy stepUpPolicy,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<RemovePhoneCommand>
{
    public async Task<Result> HandleAsync(RemovePhoneCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var caller = await CallerSession.ResolveAsync(currentUser, users, sessions, now, cancellationToken);
        if (caller.IsFailure)
        {
            return Result.Failure(caller.Error);
        }

        var (user, session) = caller.Value;
        if (user.PhoneNumber is not null)
        {
            var stepUp = stepUpPolicy.Check(await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken));
            if (stepUp.IsFailure)
            {
                return stepUp;
            }

            user.RemovePhoneNumber(now);
            auditWriter.Record(AuthAuditLog.Create(
                AuthAuditEvents.PhoneNumberRemoved,
                succeeded: true,
                now,
                userId: user.Id,
                sessionId: session.Id));
        }

        foreach (var pending in await codes.ListPendingAsync(user.Id, now, cancellationToken))
        {
            pending.Invalidate(now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
