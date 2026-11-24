using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Phone.Confirm;

/// <summary>
/// Makes the caller's pending number the account's verified number. The order is fixed:
/// <list type="number">
/// <item>The caller's user and live session (<see cref="CallerSession"/>); otherwise <c>UserErrors.NotFound</c>, answered as 401.</item>
/// <item>The caller's newest pending code; none is <see cref="PhoneErrors.InvalidCode"/>.</item>
/// <item>A wrong code counts one try in one conditional statement, which also ends the code at <c>Auth:Phone:MaxAttempts</c>, so
/// parallel guesses never exceed it; the answer is the same <see cref="PhoneErrors.InvalidCode"/> whatever the cause.</item>
/// <item>The right code is consumed in one conditional statement (of two simultaneous confirmations exactly one goes on), the number
/// becomes the user's, <c>auth.phone_number_verified</c> is audited, and one save commits it.</item>
/// <item>When another account holds the number, <see cref="PhoneErrors.PhoneNumberTaken"/> without a save; when one verifies it between
/// that check and the save, the unique index refuses the save with the same answer. Either way the code stays used and the caller's
/// previous number stays.</item>
/// </list>
/// </summary>
internal sealed class ConfirmPhoneCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IPhoneVerificationCodeRepository codes,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    IOptions<PhoneOptions> options,
    TimeProvider timeProvider) : ICommandHandler<ConfirmPhoneCommand>
{
    public async Task<Result> HandleAsync(ConfirmPhoneCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var caller = await CallerSession.ResolveAsync(currentUser, users, sessions, now, cancellationToken);
        if (caller.IsFailure)
        {
            return Result.Failure(caller.Error);
        }

        var (user, session) = caller.Value;
        var code = await codes.GetPendingAsync(user.Id, PhoneCodePurpose.Verify, now, cancellationToken);
        if (code is null)
        {
            return Result.Failure(PhoneErrors.InvalidCode);
        }

        if (!code.Matches(command.Code))
        {
            await codes.TryCountWrongAttemptAsync(code.Id, options.Value.MaxAttempts, now, cancellationToken);
            return Result.Failure(PhoneErrors.InvalidCode);
        }

        if (!await codes.TryConsumeAsync(code.Id, now, cancellationToken))
        {
            return Result.Failure(PhoneErrors.InvalidCode);
        }

        code.Consume(now);

        // Checked before the save, so only a true race reaches the unique index, whose refusal EF Core logs with the number in it.
        if (await users.IsPhoneNumberTakenAsync(code.PhoneNumber, user.Id, cancellationToken))
        {
            return Result.Failure(PhoneErrors.PhoneNumberTaken);
        }

        user.VerifyPhoneNumber(code.PhoneNumber, now);
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.PhoneNumberVerified,
            succeeded: true,
            now,
            userId: user.Id,
            sessionId: session.Id));

        // One save: the number, the code's state and the audit row; the unique index decides a number another account verified first.
        return await unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.UserPhoneNumber, cancellationToken)
            ? Result.Success()
            : Result.Failure(PhoneErrors.PhoneNumberTaken);
    }
}
