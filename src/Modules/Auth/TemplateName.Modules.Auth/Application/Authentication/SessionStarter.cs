using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication.Login;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Authentication;

/// <summary>
/// The success path of every sign-in, shared by login (an account without two-step verification) and the verify of a sign-in challenge:
/// it records the successful sign-in on the user (failures and lockout cleared, <c>LastLoginAt</c> set), starts a session from the
/// client's user agent and address with a first refresh token (only its hash is stored) and issues the access token, whose
/// <c>auth_time</c> is <c>now</c> and whose <c>amr</c> is <c>authMethods</c>. It saves nothing and audits nothing: the caller adds its
/// audit rows and saves everything at once, so a lost save keeps none of it. A sign-in that is not through a trusted device from a
/// device none of the user's stored sessions used raises <c>NewDeviceSignedInDomainEvent</c> on the new session (D15), unless it is the
/// account's first sign-in ever: no stored session and no earlier <c>LastLoginAt</c>.
/// </summary>
internal sealed class SessionStarter(
    ISessionRepository sessions,
    ISecureTokenService tokens,
    IAccessTokenIssuer accessTokens,
    IClientContext client,
    IOptions<RefreshTokenOptions> options)
{
    /// <summary>Starts a session of <paramref name="user"/> on <paramref name="deviceName"/>.</summary>
    /// <param name="user">The tracked user who just proved every factor the sign-in needs.</param>
    /// <param name="authMethods">The <c>amr</c> values in the <see cref="UserSession.AuthMethods"/> format (<see cref="AuthenticationMethods.Join"/>).</param>
    /// <param name="deviceName">The device name the client gave (<c>Unknown</c> when it gave none); a longer one is cut to its column.</param>
    /// <param name="viaTrustedDevice">
    /// Whether a remembered device stood in for the second factor. Such a sign-in is never a new device (the user already proved this
    /// device once), so nothing is asked or raised.
    /// </param>
    /// <param name="now">The instant of the sign-in: the session's start and the token's <c>auth_time</c>.</param>
    /// <param name="cancellationToken">Cancels the device lookup.</param>
    public async Task<StartedSession> StartAsync(
        User user,
        string authMethods,
        string deviceName,
        bool viaTrustedDevice,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Read before this sign-in is recorded: whether the account signed in before, whatever is left of its sessions.
        var signedInBefore = user.LastLoginAt is not null;
        user.RecordSuccessfulSignIn(now);

        var refreshToken = tokens.Generate();
        var refreshTokens = options.Value;
        var (session, firstToken) = UserSession.Start(
            user.Id,
            authMethods,
            deviceName,
            client.UserAgent,
            client.IpAddress,
            user.SecurityStamp,
            refreshToken.Hash,
            refreshTokens.SlidingLifetime,
            refreshTokens.AbsoluteLifetime,
            now);

        // Before the session is added, so it can never match itself. The stored (cut) values are the ones compared (D15). No stored
        // session is the account's first sign-in only when it never signed in before: the cleanup deletes every session of a dormant
        // account, and its next sign-in, from whatever device, is still a new device.
        if (!viaTrustedDevice)
        {
            var familiarity = await sessions.GetDeviceFamiliarityAsync(user.Id, session.DeviceName, session.UserAgent, cancellationToken);
            if (familiarity == DeviceFamiliarity.New || (familiarity == DeviceFamiliarity.NoStoredSession && signedInBefore))
            {
                session.NoteNewDevice();
            }
        }

        sessions.Add(session);

        var accessToken = accessTokens.Issue(new AccessTokenRequest(user.Id, session.Id, user.SecurityStamp, authMethods, now, user.Locale));

        return new StartedSession(
            session,
            new LoginResponse(accessToken.Value, accessToken.ExpiresAt, refreshToken.Value, firstToken.ExpiresAt, session.Id));
    }
}
