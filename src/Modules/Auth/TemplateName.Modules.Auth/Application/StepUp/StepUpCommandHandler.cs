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
/// code for one with it, a provider's proof (social login decision D8) for one with neither a password nor two-step verification;
/// anything else is <see cref="MfaErrors.StepUpMethodNotAllowed"/> and is not counted.</item>
/// <item>A locked account gets the method's failure answer without any verification and without counting; for a provider's proof that
/// is <see cref="ExternalLoginErrors.InvalidToken"/>, and the proof is not verified, so no nonce is spent.</item>
/// <item>A provider's proof comes from the catalog's enabled providers (otherwise <see cref="ExternalLoginErrors.ProviderNotFound"/>) and
/// is verified, which spends its nonce; its identity must be one of the caller's logins, otherwise
/// <see cref="ExternalLoginErrors.StepUpIdentityMismatch"/>. A refused proof is not counted in the lockout counter: it is a verified token,
/// not a guess, and the per-address limit bounds the tries. The session's <c>amr</c> becomes <c>fed</c>.</item>
/// <item>A wrong password or code counts in the lockout counter like a wrong password at login (D7, D12), by the same atomic statement;
/// the failure that locks raises the lockout event.</item>
/// </list>
/// Every refusal after the caller is known is audited (<c>auth.step_up_failed</c>) and counted in <c>auth.step_ups</c>, and so is a success.
/// A password whose hash asks for a rehash is not rehashed here: login does that, and a step-up never writes the user row, so its save
/// cannot conflict. Neither a proof nor a subject reaches an audit row: a provider's step-up records the provider's name only.
/// </summary>
internal sealed class StepUpCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IMfaMethodRepository mfaMethods,
    MfaFactorVerifier factorVerifier,
    IPasswordHasher passwordHasher,
    IExternalProviderCatalog catalog,
    IExternalIdentityVerifier identityVerifier,
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
    private const string ExternalFactor = "external";

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
        var byProvider = command.HasProviderProof;
        var factor = byProvider ? ExternalFactor : password is not null ? PasswordFactor : proof.Code is not null ? TotpFactor : RecoveryCodeFactor;

        var hasConfirmedMfa = await mfaMethods.HasConfirmedAsync(userId, cancellationToken);

        // A provider's proof is for an account with no password and no confirmed method: it has nothing else to prove with. The validator
        // refuses a second proof beside the provider's; a handler called without it fails closed all the same.
        var methodAllowed = byProvider
            ? !hasConfirmedMfa && !user.HasPassword && password is null && proof.Code is null && proof.RecoveryCode is null
            : hasConfirmedMfa != (password is not null);
        if (!methodAllowed)
        {
            return await RejectAsync(user, session, FactorDetails(factor), MfaErrors.StepUpMethodNotAllowed, MfaErrors.StepUpMethodNotAllowed.Code, now, cancellationToken);
        }

        var failure = byProvider ? ExternalLoginErrors.InvalidToken : hasConfirmedMfa ? MfaErrors.InvalidCode : UserErrors.CurrentPasswordIncorrect;
        if (user.IsLockedOut(now))
        {
            // Nothing is verified (and no nonce spent), so a guesser learns nothing while the lockout lasts, and nothing is counted: it
            // neither grows nor ends. A locked account's proof is answered like a wrong one.
            return await RejectAsync(user, session, FactorDetails(factor), failure, LockedFailureReason, now, cancellationToken);
        }

        string details;
        string? methods;
        if (byProvider)
        {
            // An unknown and a disabled provider are alike to the catalog, and the name from here on is the catalog's, not the request's.
            if (catalog.Find(command.Provider ?? string.Empty) is not { } provider)
            {
                return await RejectAsync(user, session, FactorDetails(factor), ExternalLoginErrors.ProviderNotFound, ExternalLoginErrors.ProviderNotFound.Code, now, cancellationToken);
            }

            details = FactorDetails(factor, provider.Name);
            var verified = await VerifyIdentityAsync(user, provider, command, cancellationToken);
            if (verified.IsFailure)
            {
                // A verified token is not a guess, so nothing is counted toward the lockout (the per-address limit bounds the tries). A nonce
                // may be spent by now, so the refusal is stored whatever the client does.
                return await RejectAsync(user, session, details, verified.Error, verified.Error.Code, now, CancellationToken.None);
            }

            methods = AuthenticationMethods.Federated;
        }
        else
        {
            details = FactorDetails(factor);
            methods = await VerifyAsync(user, password, proof, now, cancellationToken);
            if (methods is null)
            {
                return await RejectWrongProofAsync(user, session, details, failure, now);
            }
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
            details: details));

        // The session has no concurrency token and the user row is not changed (only an event is raised on it), so this cannot conflict.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        metrics.RecordStepUp(succeeded: true);

        // The token carries auth_time in whole seconds; StepUpPolicy measures MaxAge from that claim.
        var authTime = DateTimeOffset.FromUnixTimeSeconds(session.AuthenticatedAt.ToUnixTimeSeconds());
        return new StepUpResponse(accessToken.Value, accessToken.ExpiresAt, authTime + stepUpOptions.Value.MaxAge);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The audit details: the factor and, for a provider's proof, the provider's name; never a token, a nonce or a subject.</summary>
    private static string FactorDetails(string factor, string? provider = null) =>
        provider is null ? JsonSerializer.Serialize(new { factor }) : JsonSerializer.Serialize(new { factor, provider });

    /// <summary>
    /// Verifies the provider's proof (which spends its nonce when it holds) and checks that the identity is one of the caller's own
    /// logins, compared as stored: the provider's name, ordinally the subject.
    /// </summary>
    private async Task<Result> VerifyIdentityAsync(User user, ExternalProvider provider, StepUpCommand command, CancellationToken cancellationToken)
    {
        var verification = await identityVerifier.VerifyAsync(
            provider,
            ExternalProof.Create(command.IdToken, command.AccessToken, command.Nonce),
            cancellationToken);
        if (verification.IsFailure)
        {
            return Result.Failure(verification.Error);
        }

        var identity = verification.Value;
        var isCallers = user.ExternalLogins.Any(login =>
            login.Provider == identity.Provider && string.Equals(login.Subject, identity.Subject, StringComparison.Ordinal));
        return isCallers ? Result.Success() : Result.Failure(ExternalLoginErrors.StepUpIdentityMismatch);
    }

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

    private async Task<Result<StepUpResponse>> RejectWrongProofAsync(User user, UserSession session, string details, Error error, DateTimeOffset now)
    {
        // One atomic statement, as at login, so simultaneous wrong codes are each counted; the tracked user is left unchanged, so the
        // save below only inserts. It and the save ignore the request's cancellation: a guesser who disconnects is still counted.
        var lockout = lockoutOptions.Value;
        var counted = await users.RecordFailedSignInAsync(user.Id, now, lockout.MaxFailedAttempts, lockout.Duration, CancellationToken.None);
        if (counted is { LockoutEnd: { } lockoutEnd })
        {
            user.NoteLockedOut(lockoutEnd);
        }

        RecordFailure(user, session, details, error.Code, now);
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
        string details,
        Error error,
        string failureReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RecordFailure(user, session, details, failureReason, now);

        // Nothing but the audit row is written on these paths, which cannot conflict.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        metrics.RecordStepUp(succeeded: false);

        return Result.Failure<StepUpResponse>(error);
    }

    private void RecordFailure(User user, UserSession session, string details, string failureReason, DateTimeOffset now) =>
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.StepUpFailed,
            succeeded: false,
            now,
            userId: user.Id,
            failureReason: failureReason,
            sessionId: session.Id,
            details: details));
}
