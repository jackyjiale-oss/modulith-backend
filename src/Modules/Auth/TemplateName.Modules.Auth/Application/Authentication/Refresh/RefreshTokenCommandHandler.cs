using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication.Login;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Authentication.Refresh;

/// <summary>
/// Rotates a refresh token. The order is fixed (Ruling R7): load the session with its whole chain, load the user and check the session
/// (<see cref="UserSession.ValidateRefresh"/>: revoked, stamp, reuse, expiry), claim the token in the database, then rotate the instance
/// already loaded; it is never reloaded after the claim, which would show the token used and look like reuse. Of two requests with the
/// same token only one claim succeeds; the other is treated as reuse and revokes the session. Every outcome is audited and saved, and
/// every save ignores the request's cancellation, so a client that disconnects cannot stop a revocation (or a claimed rotation) from
/// being stored.
/// </summary>
internal sealed class RefreshTokenCommandHandler(
    ISessionRepository sessions,
    IUserRepository users,
    ISecureTokenService tokenService,
    IAccessTokenIssuer accessTokenIssuer,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    IAuthMetrics metrics,
    IOptions<RefreshTokenOptions> refreshTokenOptions,
    TimeProvider timeProvider) : ICommandHandler<RefreshTokenCommand, LoginResponse>
{
    /// <summary>The audit failure reason when the user's security stamp no longer matches the session's snapshot.</summary>
    internal const string StampChangedFailureReason = "security_stamp_changed";

    /// <summary>The audit failure reason when the session's user is gone (soft-deleted) or suspended.</summary>
    internal const string AccountUnavailableFailureReason = "account_unavailable";

    public async Task<Result<LoginResponse>> HandleAsync(RefreshTokenCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var presentedHash = tokenService.Hash(command.RefreshToken);

        // An unknown token and a token of an ended session give the same answer: nothing tells whether a token ever existed.
        var session = await sessions.GetByRefreshTokenHashAsync(presentedHash, cancellationToken);
        if (session is null)
        {
            return await FailAsync(null, SessionErrors.InvalidRefreshToken, SessionErrors.InvalidRefreshToken.Code, now);
        }

        var user = await users.GetByIdAsync(session.UserId, cancellationToken);
        if (user is null || user.Status == UserStatus.Suspended)
        {
            // Only an administrator deletes or suspends an account; its sessions end with it. Revoke is idempotent.
            session.Revoke(SessionRevokedReason.AdminRevoked, now);
            return await FailAsync(session, SessionErrors.InvalidRefreshToken, AccountUnavailableFailureReason, now);
        }

        var wasRevoked = session.RevokedAt is not null;
        var validated = session.ValidateRefresh(presentedHash, user.SecurityStamp, now);
        if (validated.IsFailure)
        {
            return await RejectAsync(session, validated.Error, revokedNow: !wasRevoked && session.RevokedAt is not null, now);
        }

        // From here on the token may be spent in the database, so the outcome must be stored whatever the client does.
        if (!await sessions.TryClaimRefreshTokenAsync(validated.Value.Id, now, CancellationToken.None))
        {
            // Another request claimed the token between our read and this statement: a used token presented again.
            session.ReportTokenReuse(now);
            return await RejectAsync(session, SessionErrors.RefreshTokenReused, revokedNow: true, now);
        }

        var refreshToken = tokenService.Generate();
        var rotated = session.Rotate(presentedHash, refreshToken.Hash, user.SecurityStamp, refreshTokenOptions.Value.SlidingLifetime, now);
        if (rotated.IsFailure)
        {
            // Not reachable: the loaded instance passed the same checks a moment ago and nothing changed it since.
            return await RejectAsync(session, rotated.Error, revokedNow: false, now);
        }

        // amr and auth_time describe the sign-in, so they come from the session, not from this refresh.
        var accessToken = accessTokenIssuer.Issue(
            new AccessTokenRequest(user.Id, session.Id, session.SecurityStamp, session.AuthMethods, session.CreatedAt, user.Locale));

        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.TokenRefreshed, succeeded: true, now, userId: user.Id, sessionId: session.Id));

        // The session and its tokens have no concurrency token, so this save cannot conflict with a parallel refresh, logout or
        // revocation: each writes only its own columns and rows, and a revoked session refuses every token it holds.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return new LoginResponse(accessToken.Value, accessToken.ExpiresAt, refreshToken.Value, rotated.Value.ExpiresAt, session.Id);
    }

    private async Task<Result<LoginResponse>> RejectAsync(UserSession session, Error error, bool revokedNow, DateTimeOffset now)
    {
        if (error == SessionErrors.RefreshTokenReused)
        {
            metrics.RecordTokenReuse();
            auditWriter.Record(AuthAuditLog.Create(
                AuthAuditEvents.TokenReuseDetected,
                succeeded: false,
                now,
                userId: session.UserId,
                failureReason: error.Code,
                sessionId: session.Id));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            return Result.Failure<LoginResponse>(error);
        }

        // The only other failure that revokes is a security stamp that changed (a password change or a suspension).
        var failureReason = revokedNow ? StampChangedFailureReason : error.Code;
        return await FailAsync(session, error, failureReason, now);
    }

    private async Task<Result<LoginResponse>> FailAsync(UserSession? session, Error error, string failureReason, DateTimeOffset now)
    {
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.RefreshFailed,
            succeeded: false,
            now,
            userId: session?.UserId,
            failureReason: failureReason,
            sessionId: session?.Id));
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Failure<LoginResponse>(error);
    }
}
