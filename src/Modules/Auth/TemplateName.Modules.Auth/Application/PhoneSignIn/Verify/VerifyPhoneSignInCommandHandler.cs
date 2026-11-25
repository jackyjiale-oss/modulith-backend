using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.PhoneSignIn.Verify;

/// <summary>
/// Trades a texted sign-in code for a session, or for a two-step verification challenge whose first factor is <c>sms</c> (spec 5.3).
/// Every refusal is the one <see cref="PhoneErrors.InvalidCode"/>, so an unknown number, a locked account, a member of a denied role and
/// a wrong code look alike; only a suspended account that presented the right code is told so (403), as a password login tells it after
/// the right password. A wrong code counts on the code and on the account's lockout, both in one atomic statement each.
/// </summary>
internal sealed class VerifyPhoneSignInCommandHandler(
    IUserRepository users,
    IPhoneVerificationCodeRepository codes,
    IMfaMethodRepository mfaMethods,
    IMfaChallengeRepository challenges,
    ISecureTokenService tokenService,
    SessionStarter sessionStarter,
    PhoneSignInGate gate,
    IAuthAuditWriter auditWriter,
    IAuthMetrics metrics,
    IUnitOfWork unitOfWork,
    IOptions<PhoneOptions> phoneOptions,
    IOptions<LockoutOptions> lockoutOptions,
    IOptions<MfaOptions> mfaOptions,
    TimeProvider timeProvider) : ICommandHandler<VerifyPhoneSignInCommand, LoginResult>
{
    private const string DefaultDeviceName = "Unknown";
    private const int MaxSaveAttempts = 2;
    private static readonly string SmsDetails = JsonSerializer.Serialize(new { method = AuthenticationMethods.Sms });

    public async Task<Result<LoginResult>> HandleAsync(VerifyPhoneSignInCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var settings = phoneOptions.Value;

        // An invalid number is the same refusal as an unknown one: verify never says more than send.
        var user = PhoneNumberRules.TryNormalize(command.PhoneNumber, settings.AllowedRegionSet(), out var number)
            ? await users.GetByPhoneNumberAsync(number, cancellationToken)
            : null;
        var code = user is null ? null : await codes.GetPendingAsync(user.Id, PhoneCodePurpose.SignIn, now, cancellationToken);

        // A code belongs to the number it was sent to: one sent before the number changed is dead.
        if (user is null || code is null || !string.Equals(code.PhoneNumber, user.PhoneNumber, StringComparison.Ordinal))
        {
            PhoneVerificationCode.SpendMatchCost(command.Code);
            return await RefuseAsync(user, "no_code", now);
        }

        // A locked account is refused before the code is looked at: nothing is counted or spent, as for a locked password login.
        if (user.IsLockedOut(now))
        {
            return await RefuseAsync(user, "locked", now);
        }

        // The try is claimed before the code is compared, so guesses arriving together get at most MaxAttempts comparisons of this code
        // between them; a code whose tries are used up is no code at all.
        if (!await codes.TryClaimAttemptAsync(code.Id, settings.MaxAttempts, now, CancellationToken.None))
        {
            PhoneVerificationCode.SpendMatchCost(command.Code);
            return await RefuseAsync(user, "no_code", now);
        }

        if (!code.Matches(command.Code))
        {
            await CountWrongCodeAsync(user, now);
            return await RefuseAsync(user, "wrong_code", now);
        }

        // The right code is spent in the database first, so of two requests with one code exactly one goes on.
        if (!await codes.TryConsumeAsync(code.Id, now, CancellationToken.None))
        {
            return await RefuseAsync(user, "lost_race", now);
        }

        code.Consume(now);

        var deviceName = command.DeviceName ?? DefaultDeviceName;
        for (var attempt = 1; ; attempt++)
        {
            // The unconfirmed-email refusal of a password login does not apply: the verified number proved the holder. Suspension does.
            if (user.Status == UserStatus.Suspended)
            {
                await RecordFailureAsync(user, "inactive", now);
                return Result.Failure<LoginResult>(UserErrors.AccountInactive);
            }

            if (await gate.IsDeniedAsync(user, cancellationToken))
            {
                return await RefuseAsync(user, "role_denied", now);
            }

            var completed = await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken)
                ? await ChallengeAsync(user, deviceName, now)
                : await StartAsync(user, deviceName, now);
            if (completed is not null)
            {
                return completed;
            }

            // Nothing was saved and the pending changes are gone, but the code stays spent. A simultaneous wrong guess counts on this
            // user's row by one atomic statement, which is enough to lose a save that carries the row's old version: read the user again
            // and go on once, while it could still sign in with this code (the number the code was sent to, not locked meanwhile).
            var reread = attempt < MaxSaveAttempts ? await users.GetByIdAsync(user.Id, CancellationToken.None) : null;
            if (reread is null || !string.Equals(reread.PhoneNumber, code.PhoneNumber, StringComparison.Ordinal) || reread.IsLockedOut(now))
            {
                return await RefuseAsync(user, "lost_race", now);
            }

            user = reread;
        }
    }

    /// <summary>
    /// The challenge of an account with two-step verification, first factor <c>sms</c>: <c>POST /api/v1/auth/mfa/verify</c> answers it and
    /// records <c>sms otp mfa</c>. No session and no recorded sign-in until a code is verified, so the failure count stays. Null when the
    /// save lost to another change of the user.
    /// </summary>
    private async Task<Result<LoginResult>?> ChallengeAsync(User user, string deviceName, DateTimeOffset now)
    {
        var token = tokenService.Generate();
        var challenge = MfaChallenge.Issue(
            user.Id,
            token.Hash,
            user.SecurityStamp,
            deviceName,
            AuthenticationMethods.Sms,
            mfaOptions.Value.ChallengeLifetime,
            now);
        challenges.Add(challenge);
        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.MfaChallengeIssued, succeeded: true, now, userId: user.Id, details: SmsDetails));

        if (!await unitOfWork.TrySaveChangesAsync(CancellationToken.None))
        {
            return null;
        }

        metrics.RecordPhoneSignIn(PhoneSignInOutcome.Challenged);
        return LoginResult.Challenge(token.Value, challenge.ExpiresAt);
    }

    /// <summary>The session of an account without two-step verification, <c>amr</c> <c>sms</c>. Null when the save lost to another change of the user.</summary>
    private async Task<Result<LoginResult>?> StartAsync(User user, string deviceName, DateTimeOffset now)
    {
        var started = await sessionStarter.StartAsync(user, AuthenticationMethods.Sms, deviceName, viaTrustedDevice: false, now, CancellationToken.None);
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.LoginSucceeded,
            succeeded: true,
            now,
            userId: user.Id,
            sessionId: started.Session.Id,
            details: SmsDetails));

        // Another request changed this user between our read and this save: nothing was saved and the code stays spent.
        if (!await unitOfWork.TrySaveChangesAsync(CancellationToken.None))
        {
            return null;
        }

        metrics.RecordPhoneSignIn(PhoneSignInOutcome.SignedIn);
        return LoginResult.Signed(started.Tokens);
    }

    // A wrong code is a guess: its try on the code was claimed before the comparison, and it also counts as one failure on the account,
    // in one atomic statement that ignores the request's cancellation, so a guesser who disconnects is still counted.
    private async Task CountWrongCodeAsync(User user, DateTimeOffset now)
    {
        var lockout = lockoutOptions.Value;
        var counted = await users.RecordFailedSignInAsync(user.Id, now, lockout.MaxFailedAttempts, lockout.Duration, CancellationToken.None);
        if (counted is { LockoutEnd: { } lockoutEnd })
        {
            user.NoteLockedOut(lockoutEnd);
            auditWriter.Record(AuthAuditLog.Create(
                AuthAuditEvents.LockedOut,
                succeeded: false,
                now,
                userId: user.Id,
                details: JsonSerializer.Serialize(new { lockoutEnd = lockoutEnd.ToString("O", CultureInfo.InvariantCulture) })));
        }
    }

    private async Task<Result<LoginResult>> RefuseAsync(User? user, string reason, DateTimeOffset now)
    {
        await RecordFailureAsync(user, reason, now);
        return Result.Failure<LoginResult>(PhoneErrors.InvalidCode);
    }

    // Only inserts (the audit row, a lockout event's outbox row) and the code's state: every refusal keeps its audit row.
    private async Task RecordFailureAsync(User? user, string reason, DateTimeOffset now)
    {
        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.PhoneSignInFailed, succeeded: false, now, userId: user?.Id, failureReason: reason));
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        metrics.RecordPhoneSignIn(PhoneSignInOutcome.Refused);
    }
}
