using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.StepUp;

/// <summary>
/// The rule a sensitive action checks before it runs (plan decision D10, ADR 0023): the caller's access token proves a recent enough
/// identity check. It reads only the signed token (<see cref="ICurrentUser.AuthTime"/> and <see cref="ICurrentUser.AuthMethods"/>),
/// so it costs no database read; the caller passes whether the account has two-step verification on, because only it knows. A handler
/// calls it, not an endpoint filter, so an action can apply it to some accounts only (D11: changing the password of an MFA account).
/// </summary>
internal sealed class StepUpPolicy(ICurrentUser currentUser, IOptions<StepUpOptions> options, TimeProvider timeProvider)
{
    /// <summary>
    /// Success when the token has an <c>auth_time</c>, less than <c>Auth:StepUp:MaxAge</c> has passed since it (at exactly
    /// <c>MaxAge</c> it has expired), and, for an account with a confirmed MFA method (<paramref name="hasConfirmedMfa"/>), the
    /// token's <c>amr</c> holds <c>otp</c> or <c>rcvr</c>: a password, or a trusted device, does not satisfy the step-up of such an
    /// account. Otherwise <see cref="MfaErrors.StepUpRequired"/> (403); the client calls <c>POST /api/v1/auth/step-up</c> and retries
    /// with the new access token.
    /// </summary>
    public Result Check(bool hasConfirmedMfa)
    {
        if (currentUser.AuthTime is not { } authTime || timeProvider.GetUtcNow() - authTime >= options.Value.MaxAge)
        {
            return Result.Failure(MfaErrors.StepUpRequired);
        }

        if (hasConfirmedMfa
            && !currentUser.AuthMethods.Contains(AuthenticationMethods.OneTimePassword, StringComparer.Ordinal)
            && !currentUser.AuthMethods.Contains(AuthenticationMethods.RecoveryCode, StringComparer.Ordinal))
        {
            return Result.Failure(MfaErrors.StepUpRequired);
        }

        return Result.Success();
    }
}
