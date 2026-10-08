using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.Modules.Auth.Application.Admin.Users;

/// <summary>
/// Issues a password-reset code for a user on an administrator's behalf, exactly as forgot-password does: the pending reset codes are
/// invalidated, only the token's hash is stored, the token travels encrypted inside the issued event (ADR 0017) and
/// <c>SendVerificationEmailDomainEventHandler</c> emails the reset link, valid for <see cref="VerificationOptions.PasswordResetLifetime"/>.
/// The caller saves. There is no cooldown: an administrator's request is deliberate and limited by the permission.
/// </summary>
internal sealed class PasswordResetLinkIssuer(
    IVerificationCodeRepository verificationCodes,
    ISecureTokenService tokenService,
    ISecretProtector secretProtector,
    IClientContext clientContext,
    IOptions<VerificationOptions> verificationOptions)
{
    public async Task IssueAsync(User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var pending in await verificationCodes.GetPendingAsync(user.Id, VerificationPurpose.PasswordReset, now, cancellationToken))
        {
            pending.Invalidate(now);
        }

        var token = tokenService.Generate();
        verificationCodes.Add(VerificationCode.Issue(
            user.Id,
            VerificationPurpose.PasswordReset,
            user.NormalizedEmail,
            token.Hash,
            secretProtector.Protect(token.Value),
            verificationOptions.Value.PasswordResetLifetime,
            clientContext.IpAddress,
            now));
    }
}
