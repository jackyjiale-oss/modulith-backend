using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Phone;

/// <summary>
/// What follows a change of the verified phone number, which can sign the account in. Every pending code of both purposes ends (a code
/// belongs to the number it was sent to). A replaced or removed number also ends whatever it opened: every other active session of the
/// user is revoked with <see cref="SessionRevokedReason.PhoneNumberChanged"/> and the current one adopts the stamp the change rotated,
/// as after unlinking a social login. Adding a first number ends no session. The caller saves.
/// </summary>
internal sealed class PhoneChangeEffects(ISessionRepository sessions, IPhoneVerificationCodeRepository codes)
{
    /// <summary>Applies the effects of <paramref name="change"/> (null: the number did not change) and returns the number of sessions revoked.</summary>
    public async Task<int> ApplyAsync(
        User user,
        PhoneNumberChange? change,
        Guid currentSessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        foreach (var pending in await codes.ListPendingAsync(user.Id, purpose: null, now, cancellationToken))
        {
            pending.Invalidate(now);
        }

        if (change is not (PhoneNumberChange.Replaced or PhoneNumberChange.Removed))
        {
            return 0;
        }

        var revoked = 0;
        foreach (var session in await sessions.GetActiveByUserAsync(user.Id, now, cancellationToken))
        {
            if (session.Id == currentSessionId)
            {
                session.AdoptSecurityStamp(user.SecurityStamp);
                continue;
            }

            session.Revoke(SessionRevokedReason.PhoneNumberChanged, now);
            revoked++;
        }

        return revoked;
    }
}
