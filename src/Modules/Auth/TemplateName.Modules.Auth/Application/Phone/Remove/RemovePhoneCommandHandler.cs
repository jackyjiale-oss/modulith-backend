using System.Text.Json;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Phone.Remove;

/// <summary>
/// Removes the caller's verified number. The caller's user and live session first (<see cref="CallerSession"/>); a verified number
/// needs a fresh step-up (<see cref="StepUpPolicy"/>, proved with a code when the account has two-step verification), and its removal
/// is audited as <c>auth.phone_number_removed</c>. Every pending code ends with it, and so does a pending code when there is no number
/// (that also needs the step-up, as starting the verification did); with neither a number nor a pending code nothing changes, nothing is
/// asked and the answer is a success. One save.
/// </summary>
internal sealed class RemovePhoneCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    PhoneChangeEffects changeEffects,
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

        // Ending a pending verification changes the account as starting one did (adding a number always needs a step-up), so a step-up
        // is also needed when only pending codes are there. Neither a number nor a code: nothing changes and nothing is asked.
        if (user.PhoneNumber is not null || (await codes.ListPendingAsync(user.Id, purpose: null, now, cancellationToken)).Count > 0)
        {
            var stepUp = stepUpPolicy.Check(await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken));
            if (stepUp.IsFailure)
            {
                return stepUp;
            }
        }

        // The pending codes end whether or not there was a number; the sessions only when a number was removed.
        var removed = user.RemovePhoneNumber(now);
        var revokedSessionCount = await changeEffects.ApplyAsync(
            user,
            removed ? PhoneNumberChange.Removed : null,
            session.Id,
            now,
            cancellationToken);
        if (removed)
        {
            auditWriter.Record(AuthAuditLog.Create(
                AuthAuditEvents.PhoneNumberRemoved,
                succeeded: true,
                now,
                userId: user.Id,
                sessionId: session.Id,
                details: JsonSerializer.Serialize(new { revokedSessionCount })));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
