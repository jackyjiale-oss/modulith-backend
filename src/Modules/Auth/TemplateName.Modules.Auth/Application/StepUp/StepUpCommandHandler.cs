using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.StepUp;

/// <summary>
/// Proves the caller's identity again and answers with a new access token of the same session whose <c>auth_time</c> is now and whose
/// <c>amr</c> is what was proved (plan decisions D9, D10 and D12, ADR 0023). The order is fixed:
/// <list type="number">
/// <item>The caller's user and session: an unknown, soft-deleted or suspended user, or a token whose session is not an active session
/// of that user holding the user's current security stamp, is <see cref="UserErrors.NotFound"/>, which the endpoint answers like a
/// request without a valid token (the <c>PUT me</c> pattern).</item>
/// <item>The factor must be the strongest the account has: a password for an account without two-step verification, a TOTP or recovery
/// code for one with it; anything else is <see cref="MfaErrors.StepUpMethodNotAllowed"/> and is not counted.</item>
/// <item>A locked account gets the method's failure answer without any verification and without counting.</item>
/// <item>A wrong password or code counts in the lockout counter like a wrong password at login (D7, D12), by the same atomic statement;
/// the failure that locks raises the lockout event.</item>
/// </list>
/// Every 400 is audited (<c>auth.step_up_failed</c>) and counted in <c>auth.step_ups</c>, and so is a success. A password whose hash
/// asks for a rehash is not rehashed here: login does that, and a step-up never writes the user row, so its save cannot conflict.
/// </summary>
internal sealed class StepUpCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IMfaMethodRepository mfaMethods,
    MfaFactorVerifier factorVerifier,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer accessTokenIssuer,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    IAuthMetrics metrics,
    IOptions<LockoutOptions> lockoutOptions,
    IOptions<StepUpOptions> stepUpOptions,
    TimeProvider timeProvider) : ICommandHandler<StepUpCommand, StepUpResponse>
{
    /// <summary>The audit failure reason of a step-up refused because the account is locked.</summary>
    internal const string LockedFailureReason = "locked";

    private const string PasswordFactor = "password";
    private const string TotpFactor = "totp";
    private const string RecoveryCodeFactor = "recovery_code";

    public async Task<Result<StepUpResponse>> HandleAsync(StepUpCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        // A step-up must never mint a token for a session that was revoked, has expired, belongs to someone else or holds an outdated stamp.
        var caller = await CallerSession.ResolveAsync(currentUser, users, sessions, now, cancellationToken);
        if (caller.IsFailure)
        {
            return Result.Failure<StepUpResponse>(caller.Error);
        }

        var (user, session) = caller.Value;
        var userId = user.Id;

        var password = NullIfBlank(command.Password);
        var proof = new MfaProof(NullIfBlank(command.Code), NullIfBlank(command.RecoveryCode));
        var factor = password is not null ? PasswordFactor : proof.Code is not null ? TotpFactor : RecoveryCodeFactor;

        var hasConfirmedMfa = await mfaMethods.HasConfirmedAsync(userId, cancellationToken);
        if (hasConfirmedMfa == (password is not null))
        {
            return await RejectAsync(user, session, factor, MfaErrors.StepUpMethodNotAllowed, MfaErrors.StepUpMethodNotAllowed.Code, now, cancellationToken);
        }

        var failure = hasConfirmedMfa ? MfaErrors.InvalidCode : UserErrors.CurrentPasswordIncorrect;
        if (user.IsLockedOut(now))
        {
            // Nothing is verified, so a guesser learns nothing while the lockout lasts, and nothing is counted: it neither grows nor ends.
            return await RejectAsync(user, session, factor, failure, LockedFailureReason, now, cancellationToken);
        }

        var methods = await VerifyAsync(user, password, proof, now, cancellationToken);
        if (methods is null)
        {
            return await RejectWrongProofAsync(user, session, factor, failure, now);
        }

        // From here on a code may be spent in the database, so the outcome is stored whatever the client does.
        session.StepUp(methods, now);
        if (methods == AuthenticationMethods.RecoveryCode)
        {
            user.NoteRecoveryCodeUsed();
        }

        var accessToken = accessTokenIssuer.Issue(
            new AccessTokenRequest(user.Id, session.Id, session.SecurityStamp, session.AuthMethods, session.AuthenticatedAt, user.Locale));

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.StepUpSucceeded,
            succeeded: true,
            now,
            userId: user.Id,
            sessionId: session.Id,
            details: FactorDetails(factor)));

        // The session has no concurrency token and the user row is not changed (only an event is raised on it), so this cannot conflict.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        metrics.RecordStepUp(succeeded: true);

        // The token carries auth_time in whole seconds; StepUpPolicy measures MaxAge from that claim.
        var authTime = DateTimeOffset.FromUnixTimeSeconds(session.AuthenticatedAt.ToUnixTimeSeconds());
        return new StepUpResponse(accessToken.Value, accessToken.ExpiresAt, authTime + stepUpOptions.Value.MaxAge);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FactorDetails(string factor) => JsonSerializer.Serialize(new { factor });

    /// <summary>The <c>amr</c> the proof establishes, or null when it is wrong (the factor verifier has already spent a right code).</summary>
    private async Task<string?> VerifyAsync(User user, string? password, MfaProof proof, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (password is not null)
        {
            return VerifyPassword(user, password) ? AuthenticationMethods.Password : null;
        }

        return await factorVerifier.VerifyAsync(user.Id, proof, now, cancellationToken) switch
        {
            MfaFactor.Totp => AuthenticationMethods.OneTimePassword,
            MfaFactor.RecoveryCode => AuthenticationMethods.RecoveryCode,
            _ => null,
        };
    }

    /// <summary>Exactly one verification: the stored hash, or the dummy for an account without a password, which never matches.</summary>
    private bool VerifyPassword(User user, string password)
    {
        if (user.PasswordHash is { } passwordHash)
        {
            return passwordHasher.Verify(passwordHash, password) != PasswordVerification.Failed;
        }

        passwordHasher.SpendVerificationCost(password);
        return false;
    }

    private async Task<Result<StepUpResponse>> RejectWrongProofAsync(User user, UserSession session, string factor, Error error, DateTimeOffset now)
    {
        // One atomic statement, as at login, so simultaneous wrong codes are each counted; the tracked user is left unchanged, so the
        // save below only inserts. It and the save ignore the request's cancellation: a guesser who disconnects is still counted.
        var lockout = lockoutOptions.Value;
        var counted = await users.RecordFailedSignInAsync(user.Id, now, lockout.MaxFailedAttempts, lockout.Duration, CancellationToken.None);
        if (counted is { LockoutEnd: { } lockoutEnd })
        {
            user.NoteLockedOut(lockoutEnd);
        }

        RecordFailure(user, session, factor, error.Code, now);
        if (counted is { LockoutEnd: { } end })
        {
            auditWriter.Record(AuthAuditLog.Create(
                AuthAuditEvents.LockedOut,
                succeeded: false,
                now,
                userId: user.Id,
                sessionId: session.Id,
                details: JsonSerializer.Serialize(new { lockoutEnd = end.ToString("O", CultureInfo.InvariantCulture) })));
        }

        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        metrics.RecordStepUp(succeeded: false);

        return Result.Failure<StepUpResponse>(error);
    }

    private async Task<Result<StepUpResponse>> RejectAsync(
        User user,
        UserSession session,
        string factor,
        Error error,
        string failureReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RecordFailure(user, session, factor, failureReason, now);

        // Nothing but the audit row is written on these paths, which cannot conflict.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        metrics.RecordStepUp(succeeded: false);

        return Result.Failure<StepUpResponse>(error);
    }

    private void RecordFailure(User user, UserSession session, string factor, string failureReason, DateTimeOffset now) =>
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.StepUpFailed,
            succeeded: false,
            now,
            userId: user.Id,
            failureReason: failureReason,
            sessionId: session.Id,
            details: FactorDetails(factor)));
}
