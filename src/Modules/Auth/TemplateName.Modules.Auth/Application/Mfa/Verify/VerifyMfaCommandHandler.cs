using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Mfa.Verify;

/// <summary>
/// The second step of a sign-in (plan decisions D6, D7, D9 and D19, ADR 0024): trades a challenge from login and a code for the tokens
/// of a new session. The order is fixed:
/// <list type="number">
/// <item>The challenge, by the SHA-256 of its token, and its user. A challenge that is unknown, expired, used, exhausted
/// (<c>Auth:Mfa:MaxChallengeAttempts</c> wrong codes) or bound to an older security stamp, and a user who is gone, cannot sign in, is
/// locked or has no confirmed method any more, are one answer, <see cref="MfaErrors.InvalidChallenge"/>; nothing is verified or counted.</item>
/// <item>The factor, through <see cref="MfaFactorVerifier"/>, which spends a right code. A wrong one counts on the challenge
/// <b>and</b> in the user's lockout counter (D7), each by one atomic statement; the failure that locks raises the lockout event.
/// The answer is <see cref="MfaErrors.InvalidCode"/>.</item>
/// <item>The challenge is consumed by one conditional update; losing it (a simultaneous verify, or the cap or the expiry reached
/// meanwhile) is <see cref="MfaErrors.InvalidChallenge"/>, and the code stays spent: the user signs in again with the next code.</item>
/// <item>The session, through <see cref="SessionStarter"/>: <c>amr</c> <c>pwd otp mfa</c> or <c>pwd rcvr mfa</c>, <c>auth_time</c> now, the
/// failure count reset; a recovery code also raises <see cref="MfaChange.RecoveryCodeUsed"/>.</item>
/// </list>
/// Every outcome is audited (<c>auth.mfa_verified</c> with <c>auth.login_succeeded</c>, or <c>auth.mfa_failed</c> with the error code as
/// the reason) and counted in <c>auth.mfa_verifications</c>. From the factor on, nothing honours the request's cancellation: a code may
/// already be spent, so the outcome is stored whatever the client does.
/// </summary>
internal sealed class VerifyMfaCommandHandler(
    IUserRepository users,
    IMfaChallengeRepository challenges,
    IMfaMethodRepository mfaMethods,
    MfaFactorVerifier factorVerifier,
    SessionStarter sessionStarter,
    ISecureTokenService tokenService,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    IAuthMetrics metrics,
    IOptions<LockoutOptions> lockoutOptions,
    IOptions<MfaOptions> mfaOptions,
    TimeProvider timeProvider) : ICommandHandler<VerifyMfaCommand, MfaVerifyResponse>
{
    /// <summary>
    /// How often the success save is tried. The first can lose to a simultaneous request of the same sign-in (a double tap) whose code
    /// lost its claim and counted a failure on the user row in between; the code and the challenge are already this request's, so the
    /// sign-in is completed once more on the user read again. A second conflict is answered like a lost challenge.
    /// </summary>
    private const int MaxSaveAttempts = 2;

    public async Task<Result<MfaVerifyResponse>> HandleAsync(VerifyMfaCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var maxAttempts = mfaOptions.Value.MaxChallengeAttempts;
        var proof = new MfaProof(NullIfBlank(command.Code), NullIfBlank(command.RecoveryCode));
        var tried = proof.Code is not null ? MfaFactor.Totp : MfaFactor.RecoveryCode;

        var challenge = await challenges.GetByTokenHashAsync(tokenService.Hash(command.ChallengeToken), cancellationToken);
        var user = challenge is null ? null : await users.GetByIdAsync(challenge.UserId, cancellationToken);
        if (challenge is null
            || user is null
            || !challenge.CanUse(user.SecurityStamp, now, maxAttempts)
            || !CanSignIn(user, challenge, now)
            || !await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken))
        {
            // One answer for every reason, and nothing counted: a dead challenge is no guess against the account.
            return await RejectChallengeAsync(challenge?.UserId, tried, now, cancellationToken);
        }

        if (await factorVerifier.VerifyAsync(user.Id, proof, now, cancellationToken) is not { } factor)
        {
            return await RejectWrongCodeAsync(user, challenge, tried, now);
        }

        // From here on the code is spent in the database, so nothing below honours the request's cancellation.
        if (!await challenges.TryConsumeAsync(challenge.Id, now, maxAttempts, CancellationToken.None))
        {
            return await RejectChallengeAsync(user.Id, tried, now, CancellationToken.None);
        }

        var methods = AuthenticationMethods.Join(
            AuthenticationMethods.Password,
            factor == MfaFactor.Totp ? AuthenticationMethods.OneTimePassword : AuthenticationMethods.RecoveryCode,
            AuthenticationMethods.MultiFactor);

        for (var attempt = 1; ; attempt++)
        {
            var started = await sessionStarter.StartAsync(user, methods, challenge.DeviceName, viaTrustedDevice: false, now, CancellationToken.None);
            if (factor == MfaFactor.RecoveryCode)
            {
                user.NoteRecoveryCodeUsed();
            }

            auditWriter.Record(AuthAuditLog.Create(
                AuthAuditEvents.MfaVerified,
                succeeded: true,
                now,
                userId: user.Id,
                sessionId: started.Session.Id,
                details: FactorDetails(factor)));
            auditWriter.Record(AuthAuditLog.Create(
                AuthAuditEvents.LoginSucceeded,
                succeeded: true,
                now,
                userId: user.Id,
                sessionId: started.Session.Id));

            if (await unitOfWork.TrySaveChangesAsync(CancellationToken.None))
            {
                metrics.RecordMfaVerification(MfaVerificationOutcome.Succeeded, factor);
                var tokens = started.Tokens;
                return new MfaVerifyResponse(
                    tokens.AccessToken,
                    tokens.AccessTokenExpiresAt,
                    tokens.RefreshToken,
                    tokens.RefreshTokenExpiresAt,
                    tokens.SessionId,
                    TrustedDeviceToken: null,
                    TrustedDeviceExpiresAt: null);
            }

            // Nothing was saved and the pending changes are gone. Read the user again and go on only while it could still sign in with
            // this challenge (a password or MFA change, a suspension or a lockout in between ends it).
            var reread = attempt < MaxSaveAttempts ? await users.GetByIdAsync(challenge.UserId, CancellationToken.None) : null;
            if (reread is null || !CanSignIn(reread, challenge, now))
            {
                return await RejectChallengeAsync(challenge.UserId, tried, now, CancellationToken.None);
            }

            user = reread;
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The user still holds the stamp the challenge was issued under, may sign in and is not locked.</summary>
    private static bool CanSignIn(User user, MfaChallenge challenge, DateTimeOffset now) =>
        string.Equals(user.SecurityStamp, challenge.SecurityStamp, StringComparison.Ordinal)
        && user.EnsureCanSignIn().IsSuccess
        && !user.IsLockedOut(now);

    private static string FactorDetails(MfaFactor factor) =>
        JsonSerializer.Serialize(new { factor = factor == MfaFactor.Totp ? "totp" : "recovery_code" });

    private async Task<Result<MfaVerifyResponse>> RejectWrongCodeAsync(User user, MfaChallenge challenge, MfaFactor tried, DateTimeOffset now)
    {
        // Two atomic statements, so simultaneous wrong codes are each counted: one on the challenge (its cap) and one in the user's
        // lockout counter (D7), which a new challenge does not reset. The tracked user is left unchanged, so the save below only inserts.
        // Neither the counts nor the save honour the request's cancellation: a guesser who disconnects is still counted.
        await challenges.RecordFailedAttemptAsync(challenge.Id, CancellationToken.None);
        var lockout = lockoutOptions.Value;
        var counted = await users.RecordFailedSignInAsync(user.Id, now, lockout.MaxFailedAttempts, lockout.Duration, CancellationToken.None);
        if (counted is { LockoutEnd: { } lockoutEnd })
        {
            user.NoteLockedOut(lockoutEnd);
        }

        RecordFailure(user.Id, tried, MfaErrors.InvalidCode, now);
        if (counted is { LockoutEnd: { } end })
        {
            auditWriter.Record(AuthAuditLog.Create(
                AuthAuditEvents.LockedOut,
                succeeded: false,
                now,
                userId: user.Id,
                details: JsonSerializer.Serialize(new { lockoutEnd = end.ToString("O", CultureInfo.InvariantCulture) })));
        }

        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        metrics.RecordMfaVerification(MfaVerificationOutcome.InvalidCode, tried);

        return Result.Failure<MfaVerifyResponse>(MfaErrors.InvalidCode);
    }

    private async Task<Result<MfaVerifyResponse>> RejectChallengeAsync(Guid? userId, MfaFactor tried, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RecordFailure(userId, tried, MfaErrors.InvalidChallenge, now);

        // Only the audit row is inserted, which cannot conflict.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        metrics.RecordMfaVerification(MfaVerificationOutcome.InvalidChallenge, factor: null);

        return Result.Failure<MfaVerifyResponse>(MfaErrors.InvalidChallenge);
    }

    private void RecordFailure(Guid? userId, MfaFactor tried, Error error, DateTimeOffset now) =>
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.MfaFailed,
            succeeded: false,
            now,
            userId: userId,
            failureReason: error.Code,
            details: FactorDetails(tried)));
}
