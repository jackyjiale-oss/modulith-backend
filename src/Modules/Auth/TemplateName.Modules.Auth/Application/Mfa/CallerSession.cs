using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Mfa;

/// <summary>
/// The signed-in caller's user and live session, resolved the same fail-closed way by every handler that changes or proves the caller's
/// two-step verification (step-up, setup, confirm, removal, regeneration).
/// </summary>
/// <param name="User">The caller's account: neither unknown, soft-deleted nor suspended.</param>
/// <param name="Session">The session the access token names: an active session of that user holding the user's current security stamp.</param>
internal sealed record CallerSession(User User, UserSession Session)
{
    /// <summary>
    /// The caller's user and session, or <see cref="UserErrors.NotFound"/> (which the endpoint answers like a request without a valid
    /// token) when there is no user or session id in the token, the user is unknown, soft-deleted or suspended, or the token's session
    /// is not an active session of that user holding the user's current stamp. The access token outlives its session (plan decision D9),
    /// so the session is checked here: a handler that keeps or renews it must never revive one that has ended, belongs to someone else or
    /// holds an outdated stamp (its next refresh would end it).
    /// </summary>
    public static async Task<Result<CallerSession>> ResolveAsync(
        ICurrentUser currentUser,
        IUserRepository users,
        ISessionRepository sessions,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(sessions);

        if (currentUser.UserId is not { } userId || currentUser.SessionId is not { } sessionId)
        {
            return Result.Failure<CallerSession>(UserErrors.NotFound(currentUser.UserId ?? Guid.Empty));
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null || user.Status == UserStatus.Suspended)
        {
            return Result.Failure<CallerSession>(UserErrors.NotFound(userId));
        }

        var session = await sessions.GetByIdAsync(sessionId, cancellationToken);
        if (session is null
            || session.UserId != userId
            || !session.IsActive(now)
            || !string.Equals(session.SecurityStamp, user.SecurityStamp, StringComparison.Ordinal))
        {
            return Result.Failure<CallerSession>(UserErrors.NotFound(userId));
        }

        return new CallerSession(user, session);
    }
}
