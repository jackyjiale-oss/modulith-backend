using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Mfa;

/// <summary>
/// What every change of a user's two-step verification does to the account (plan decision D13): the security stamp rotates, which also
/// ends the pending sign-in challenges and the remembered devices (both are bound to the stamp they were issued under), and the user's
/// active sessions are revoked at once, except the one the change was made from, which takes the new stamp and stays signed in (as a
/// password change does). Nothing is saved here: the caller's single save commits it with the change itself.
/// </summary>
internal sealed class MfaChangeEffects(ISessionRepository sessions)
{
    /// <summary>
    /// Applies <paramref name="change"/> (<c>User.ChangeMfa</c>: a new stamp and <c>MfaChangedDomainEvent</c>), revokes every active
    /// session of the user except <paramref name="keepSessionId"/> with <paramref name="reason"/>, and makes the kept session adopt the new
    /// stamp. A kept session that is no longer active is not among the active sessions and stays as it is; null keeps none.
    /// </summary>
    /// <returns>The number of sessions revoked.</returns>
    public async Task<int> ApplyAsync(
        User user,
        MfaChange change,
        Guid? keepSessionId,
        SessionRevokedReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.ChangeMfa(change, now);

        var revoked = 0;
        foreach (var session in await sessions.GetActiveByUserAsync(user.Id, now, cancellationToken))
        {
            if (session.Id == keepSessionId)
            {
                session.AdoptSecurityStamp(user.SecurityStamp);
                continue;
            }

            session.Revoke(reason, now);
            revoked++;
        }

        return revoked;
    }
}
