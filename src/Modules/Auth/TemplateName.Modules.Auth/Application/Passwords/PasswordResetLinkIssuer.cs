using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.Modules.Auth.Application.Passwords;

/// <summary>
/// The one place a password-reset link is issued, for forgot-password and for the administrator's create and forced reset: the user's
/// pending reset codes are invalidated, a new <see cref="VerificationPurpose.PasswordReset"/> code is issued of whose token only the hash
/// is stored, the token travels encrypted inside the issued event (ADR 0017) with the caller's <see cref="VerificationTrigger"/>, and the
/// outbox handlers email the reset link and publish it (<c>PasswordResetRequestedIntegrationEvent</c>, whose reason is the trigger),
/// valid for <see cref="VerificationOptions.PasswordResetLifetime"/>. The caller decides whether to issue (forgot checks its cooldown
/// first; an administrator's request has none, the permission is the limit), writes its own audit entry and saves.
/// </summary>
internal sealed class PasswordResetLinkIssuer(
    IVerificationCodeRepository verificationCodes,
    ISecureTokenService tokenService,
    ISecretProtector secretProtector,
    IClientContext clientContext,
    IOptions<VerificationOptions> verificationOptions)
{
    public async Task IssueAsync(User user, VerificationTrigger trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var pending in await verificationCodes.GetPendingAsync(user.Id, VerificationPurpose.PasswordReset, now, cancellationToken))
        {
            pending.Invalidate(now);
        }

        var token = tokenService.Generate();
        verificationCodes.Add(VerificationCode.Issue(
            user.Id,
            VerificationPurpose.PasswordReset,
            trigger,
            user.NormalizedEmail,
            token.Hash,
            secretProtector.Protect(token.Value),
            verificationOptions.Value.PasswordResetLifetime,
            clientContext.IpAddress,
            now));
    }
}
