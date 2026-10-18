using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Authentication.Login;

/// <summary>
/// Signs a user in. Every wrong combination gets the same <see cref="UserErrors.InvalidCredentials"/> after the same work: one password
/// verification always runs (the dummy one for an unknown email or an account without a password), a locked account answers like a wrong
/// password (decision D10), and the 403s come only after a correct password, so they reveal nothing to someone who does not know it.
/// Every outcome writes an audit entry and is saved, so failure counts survive the failure result; a failure is counted by one atomic
/// statement in the database, so parallel guesses are each counted and never lost to a concurrency conflict.
/// <para>
/// Only once the password is right and the account may sign in does it look at two-step verification (Plan 4): an account without a
/// confirmed method gets its session as before; one with it gets a challenge instead, and the failure count is <b>not</b> reset (decision
/// D7: only a verified second factor resets it), so starting a new challenge after every few wrong codes never earns more guesses.
/// </para>
/// </summary>
internal sealed class LoginCommandHandler(
    IUserRepository users,
    IMfaMethodRepository mfaMethods,
    IMfaChallengeRepository challenges,
    IPasswordHasher passwordHasher,
    ISecureTokenService tokenService,
    SessionStarter sessionStarter,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    IAuthMetrics metrics,
    IOptions<LockoutOptions> lockoutOptions,
    IOptions<MfaOptions> mfaOptions,
    TimeProvider timeProvider) : ICommandHandler<LoginCommand, LoginResult>
{
    /// <summary>The audit failure reason of a correct password on a locked account.</summary>
    internal const string LockedFailureReason = "locked";

    private const string DefaultDeviceName = "Unknown";

    public async Task<Result<LoginResult>> HandleAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var attemptedIdentifier = AuthAuditLog.MaskIdentifier(command.Email);
        var user = await users.GetByNormalizedEmailAsync(User.NormalizeEmail(command.Email), cancellationToken);

        // Before any other decision, so every path below has paid for exactly one verification.
        var verification = Verify(user, command.Password);
        if (user is null || verification == PasswordVerification.Failed)
        {
            return await RejectWrongCredentialsAsync(user, attemptedIdentifier, now, cancellationToken);
        }

        // From here on the password is right, so the answers below can only tell its owner something.
        if (user.IsLockedOut(now))
        {
            // Not counted again: the lockout neither grows nor ends because the right password was typed while it lasts.
            return await RejectAsync(user, LockedFailureReason, LoginOutcome.Locked, UserErrors.InvalidCredentials, attemptedIdentifier, now, cancellationToken);
        }

        var canSignIn = user.EnsureCanSignIn();
        if (canSignIn.IsFailure)
        {
            var outcome = canSignIn.Error == UserErrors.AccountInactive ? LoginOutcome.Inactive : LoginOutcome.Unverified;
            return await RejectAsync(user, canSignIn.Error.Code, outcome, canSignIn.Error, attemptedIdentifier, now, cancellationToken);
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            user.UpgradePasswordHash(passwordHasher.Hash(command.Password));
        }

        // The first read of anything two-step: only a correct password of an account that may sign in gets this far (enumeration).
        var deviceName = command.DeviceName ?? DefaultDeviceName;
        if (await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken))
        {
            return await ChallengeAsync(user, deviceName, attemptedIdentifier, now, cancellationToken);
        }

        return await SignInAsync(user, deviceName, attemptedIdentifier, now, cancellationToken);
    }

    /// <summary>
    /// Exactly one verification per attempt: the stored hash when there is one, otherwise the dummy (an account an administrator created
    /// has no password yet and is treated exactly like an unknown email, Ruling R3).
    /// </summary>
    private PasswordVerification Verify(User? user, string password)
    {
        if (user?.PasswordHash is { } passwordHash)
        {
            return passwordHasher.Verify(passwordHash, password);
        }

        passwordHasher.SpendVerificationCost(password);
        return PasswordVerification.Failed;
    }

    private async Task<Result<LoginResult>> RejectWrongCredentialsAsync(
        User? user,
        string? attemptedIdentifier,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Only an account with a password counts failures: an unknown email and an account without a password change nothing. The count
        // is one atomic statement in the database (the rules of User.RecordFailedSignIn), so failures that run at the same time are each
        // counted; the tracked user is left unchanged, so the save below cannot conflict and never writes an outdated count back.
        FailedSignIn? counted = null;
        if (user is { PasswordHash: not null })
        {
            var lockout = lockoutOptions.Value;
            counted = await users.RecordFailedSignInAsync(user.Id, now, lockout.MaxFailedAttempts, lockout.Duration, cancellationToken);
            if (counted is { LockoutEnd: { } lockoutEnd })
            {
                user.NoteLockedOut(lockoutEnd);
            }
        }

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.LoginFailed,
            succeeded: false,
            now,
            userId: user?.Id,
            failureReason: UserErrors.InvalidCredentials.Code,
            attemptedIdentifier: attemptedIdentifier));

        if (counted is { LockoutEnd: { } end })
        {
            auditWriter.Record(AuthAuditLog.Create(
                AuthAuditEvents.LockedOut,
                succeeded: false,
                now,
                userId: user!.Id,
                attemptedIdentifier: attemptedIdentifier,
                details: JsonSerializer.Serialize(new { lockoutEnd = end.ToString("O", CultureInfo.InvariantCulture) })));
        }

        // Only inserts (audit rows, the lockout event's outbox row): nothing here can conflict, so every failure keeps its audit row.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        metrics.RecordLogin(LoginOutcome.InvalidCredentials);

        return Result.Failure<LoginResult>(UserErrors.InvalidCredentials);
    }

    private async Task<Result<LoginResult>> RejectAsync(
        User user,
        string failureReason,
        LoginOutcome outcome,
        Error error,
        string? attemptedIdentifier,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.LoginFailed,
            succeeded: false,
            now,
            userId: user.Id,
            failureReason: failureReason,
            attemptedIdentifier: attemptedIdentifier));

        // The user is unchanged on these paths: only the audit row is inserted, which cannot conflict.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        metrics.RecordLogin(outcome);

        return Result.Failure<LoginResult>(error);
    }

    private async Task<Result<LoginResult>> SignInAsync(
        User user,
        string deviceName,
        string? attemptedIdentifier,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var started = await sessionStarter.StartAsync(user, AuthenticationMethods.Password, deviceName, viaTrustedDevice: false, now, cancellationToken);

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.LoginSucceeded,
            succeeded: true,
            now,
            userId: user.Id,
            attemptedIdentifier: attemptedIdentifier,
            sessionId: started.Session.Id));

        // Another request changed this user between our read and this save (a parallel login, say). Nothing was saved; answer like a
        // wrong password, because the 409 the conflict would otherwise become can only happen to an existing account.
        if (!await unitOfWork.TrySaveChangesAsync(cancellationToken))
        {
            metrics.RecordLogin(LoginOutcome.InvalidCredentials);
            return Result.Failure<LoginResult>(UserErrors.InvalidCredentials);
        }

        metrics.RecordLogin(LoginOutcome.Succeeded);

        return LoginResult.Signed(started.Tokens);
    }

    /// <summary>
    /// The password step of an account with two-step verification: a challenge (a fresh secure token, only its hash stored) bound to the
    /// user's security stamp, valid <c>Auth:Mfa:ChallengeLifetime</c>. No session, and no <see cref="User.RecordSuccessfulSignIn"/>: the
    /// failure count stays as it is until a second factor is verified (D7).
    /// </summary>
    private async Task<Result<LoginResult>> ChallengeAsync(
        User user,
        string deviceName,
        string? attemptedIdentifier,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var token = tokenService.Generate();
        var challenge = MfaChallenge.Issue(user.Id, token.Hash, user.SecurityStamp, deviceName, mfaOptions.Value.ChallengeLifetime, now);
        challenges.Add(challenge);

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.MfaChallengeIssued,
            succeeded: true,
            now,
            userId: user.Id,
            attemptedIdentifier: attemptedIdentifier));

        // The user row changes here only for a rehash; a conflict with another request is answered like the success path's.
        if (!await unitOfWork.TrySaveChangesAsync(cancellationToken))
        {
            metrics.RecordLogin(LoginOutcome.InvalidCredentials);
            return Result.Failure<LoginResult>(UserErrors.InvalidCredentials);
        }

        metrics.RecordLogin(LoginOutcome.MfaRequired);

        return LoginResult.Challenge(token.Value, challenge.ExpiresAt);
    }
}
