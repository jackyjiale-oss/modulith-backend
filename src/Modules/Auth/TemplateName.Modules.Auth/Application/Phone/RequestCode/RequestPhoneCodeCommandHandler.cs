using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Phone.RequestCode;

/// <summary>
/// Sends a 6-digit code to the number the caller wants on the account. The order is fixed:
/// <list type="number">
/// <item>The caller's user and live session (<see cref="CallerSession"/>); otherwise <c>UserErrors.NotFound</c>, answered as 401.</item>
/// <item>The number must be a valid mobile number in international form from an allowed region; it is kept in E.164. Otherwise
/// <see cref="PhoneErrors.InvalidPhoneNumber"/>.</item>
/// <item>Replacing a verified number needs a fresh step-up (phone-number sign-in will trust that number); adding the first does not.</item>
/// <item>The resend cooldown and the daily cap: otherwise <see cref="TooManyCodesError"/> with the time until the next code is allowed.</item>
/// <item>Every pending code of the caller ends, a new one is issued (only its salted hash is stored) and its requested event, carrying
/// the code encrypted with <see cref="ISecretProtector"/>, goes to the outbox in the same save, with the user's
/// <c>PhoneCodeRequestedAt</c>: writing the user row serializes simultaneous requests on its RowVersion. The plain code never leaves
/// this method.</item>
/// </list>
/// The number is not checked against other accounts here, so the route does not tell whether a number has an account: only the person
/// who receives the code learns, at confirmation, that it is taken.
/// </summary>
internal sealed class RequestPhoneCodeCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IPhoneVerificationCodeRepository codes,
    IMfaMethodRepository mfaMethods,
    StepUpPolicy stepUpPolicy,
    ICurrentUser currentUser,
    ISecretProtector secretProtector,
    IUnitOfWork unitOfWork,
    IOptions<PhoneOptions> options,
    TimeProvider timeProvider) : ICommandHandler<RequestPhoneCodeCommand, PhoneCodeResponse>
{
    private static readonly TimeSpan DailyWindow = TimeSpan.FromDays(1);

    public async Task<Result<PhoneCodeResponse>> HandleAsync(RequestPhoneCodeCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var settings = options.Value;
        var caller = await CallerSession.ResolveAsync(currentUser, users, sessions, now, cancellationToken);
        if (caller.IsFailure)
        {
            return Result.Failure<PhoneCodeResponse>(caller.Error);
        }

        var user = caller.Value.User;
        if (!PhoneNumberRules.TryNormalize(command.PhoneNumber, settings.AllowedRegionSet(), out var number))
        {
            return Result.Failure<PhoneCodeResponse>(PhoneErrors.InvalidPhoneNumber);
        }

        if (user.PhoneNumber is not null)
        {
            var stepUp = stepUpPolicy.Check(await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken));
            if (stepUp.IsFailure)
            {
                return Result.Failure<PhoneCodeResponse>(stepUp.Error);
            }
        }

        if (await RetryAfterAsync(user.Id, settings, now, cancellationToken) is { } retryAfter)
        {
            return Result.Failure<PhoneCodeResponse>(new TooManyCodesError(retryAfter));
        }

        foreach (var pending in await codes.ListPendingAsync(user.Id, PhoneCodePurpose.Verify, now, cancellationToken))
        {
            pending.Invalidate(now);
        }

        var plainCode = PhoneVerificationCode.NewCode();
        var code = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, number, plainCode, secretProtector.Protect(plainCode), settings.CodeLifetime, now);
        codes.Add(code);
        user.NotePhoneCodeRequested(now);

        // One save: the ended codes, the new code, the outbox row of its requested event and the user's request time. The user's
        // RowVersion makes a simultaneous request's save fail (409 concurrency.conflict), so the cooldown and the cap cannot be raced.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new PhoneCodeResponse(code.ExpiresAt);
    }

    /// <summary>How long until the cooldown or the daily cap allows another code, or null when one is allowed now.</summary>
    private async Task<TimeSpan?> RetryAfterAsync(Guid userId, PhoneOptions settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await codes.GetLastIssuedAtAsync(userId, cancellationToken) is { } lastIssuedAt && now - lastIssuedAt < settings.ResendCooldown)
        {
            return lastIssuedAt + settings.ResendCooldown - now;
        }

        var issuedToday = await codes.ListIssuedSinceAsync(userId, now - DailyWindow, cancellationToken);
        if (issuedToday.Count >= settings.MaxCodesPerDay)
        {
            // The next code is allowed when enough of the oldest ones have left the window to bring the count below the cap.
            return issuedToday[issuedToday.Count - settings.MaxCodesPerDay] + DailyWindow - now;
        }

        return null;
    }
}
