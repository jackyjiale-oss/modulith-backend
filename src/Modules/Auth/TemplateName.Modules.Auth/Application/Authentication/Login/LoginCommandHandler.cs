using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
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
/// </summary>
internal sealed class LoginCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IPasswordHasher passwordHasher,
    ISecureTokenService tokenService,
    IAccessTokenIssuer accessTokenIssuer,
    IAuthAuditWriter auditWriter,
    IClientContext clientContext,
    IUnitOfWork unitOfWork,
    IAuthMetrics metrics,
    IOptions<LockoutOptions> lockoutOptions,
    IOptions<RefreshTokenOptions> refreshTokenOptions,
    TimeProvider timeProvider) : ICommandHandler<LoginCommand, LoginResponse>
{
    /// <summary>The audit failure reason of a correct password on a locked account.</summary>
    internal const string LockedFailureReason = "locked";

    /// <summary>The <c>amr</c> value of a password sign-in.</summary>
    private const string PasswordAuthMethod = "pwd";

    private const string DefaultDeviceName = "Unknown";

    public async Task<Result<LoginResponse>> HandleAsync(LoginCommand command, CancellationToken cancellationToken)
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

        return await SignInAsync(user, command, verification, attemptedIdentifier, now, cancellationToken);
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

    private async Task<Result<LoginResponse>> RejectWrongCredentialsAsync(
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

        return Result.Failure<LoginResponse>(UserErrors.InvalidCredentials);
    }

    private async Task<Result<LoginResponse>> RejectAsync(
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

        return Result.Failure<LoginResponse>(error);
    }

    private async Task<Result<LoginResponse>> SignInAsync(
        User user,
        LoginCommand command,
        PasswordVerification verification,
        string? attemptedIdentifier,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        user.RecordSuccessfulSignIn(now);
        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            user.UpgradePasswordHash(passwordHasher.Hash(command.Password));
        }

        var refreshToken = tokenService.Generate();
        var refreshTokens = refreshTokenOptions.Value;
        var (session, firstToken) = UserSession.Start(
            user.Id,
            PasswordAuthMethod,
            command.DeviceName ?? DefaultDeviceName,
            clientContext.UserAgent,
            clientContext.IpAddress,
            user.SecurityStamp,
            refreshToken.Hash,
            refreshTokens.SlidingLifetime,
            refreshTokens.AbsoluteLifetime,
            now);
        sessions.Add(session);

        var accessToken = accessTokenIssuer.Issue(new AccessTokenRequest(user.Id, session.Id, user.SecurityStamp, PasswordAuthMethod, now, user.Locale));

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.LoginSucceeded,
            succeeded: true,
            now,
            userId: user.Id,
            attemptedIdentifier: attemptedIdentifier,
            sessionId: session.Id));

        // Another request changed this user between our read and this save (a parallel login, say). Nothing was saved; answer like a
        // wrong password, because the 409 the conflict would otherwise become can only happen to an existing account.
        if (!await unitOfWork.TrySaveChangesAsync(cancellationToken))
        {
            metrics.RecordLogin(LoginOutcome.InvalidCredentials);
            return Result.Failure<LoginResponse>(UserErrors.InvalidCredentials);
        }

        metrics.RecordLogin(LoginOutcome.Succeeded);

        return new LoginResponse(accessToken.Value, accessToken.ExpiresAt, refreshToken.Value, firstToken.ExpiresAt, session.Id);
    }
}
