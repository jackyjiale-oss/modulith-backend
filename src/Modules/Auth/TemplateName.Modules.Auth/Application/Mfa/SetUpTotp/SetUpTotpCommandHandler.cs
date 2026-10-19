using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Mfa.SetUpTotp;

/// <summary>
/// Starts the setup of an authenticator app (plan decisions D11 and D18). The order is fixed:
/// <list type="number">
/// <item>A fresh step-up (<see cref="StepUpPolicy.Check"/> with no confirmed method: a confirmed one is refused next, so a recent
/// sign-in or password step-up is enough), otherwise <see cref="MfaErrors.StepUpRequired"/>.</item>
/// <item>The caller's user and session, as at step-up: an unknown, soft-deleted or suspended user, or a token whose session is not an
/// active session of that user holding the user's current stamp, is <see cref="UserErrors.NotFound"/>, which the endpoint answers like
/// a request without a valid token.</item>
/// <item>A confirmed method is <see cref="MfaErrors.AlreadyEnabled"/>: one authenticator per user; replacing it is remove, then set up.</item>
/// <item>A new 20-byte secret, protected by <see cref="IMfaSecretProtector"/>, replaces the secret of a pending method in place
/// (<see cref="UserMfaMethod.RestartSetup"/>) or starts a new pending one; audit <c>auth.mfa_setup_started</c> (the method id only); save.</item>
/// </list>
/// The plain secret leaves only in the answer, as base32 and inside the <c>otpauth://</c> URI; the stamp and the sessions do not change
/// until the confirm. When a simultaneous setup inserted the user's row first, the unique index refuses this insert and nothing of it is
/// saved; this request then takes that pending row over as a second setup would, so what it answers is what is stored. A restart whose
/// method a simultaneous confirm or restart changed after it was loaded fails on the method's concurrency tokens (409
/// <c>concurrency.conflict</c>), so the secret of an enabled method is never replaced here.
/// </summary>
internal sealed class SetUpTotpCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IMfaMethodRepository mfaMethods,
    IMfaSecretProtector secretProtector,
    StepUpPolicy stepUpPolicy,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    IOptions<MfaOptions> mfaOptions,
    TimeProvider timeProvider) : ICommandHandler<SetUpTotpCommand, TotpSetupResponse>
{
    /// <summary>The label of a method set up without one.</summary>
    internal const string DefaultLabel = "Authenticator app";

    public async Task<Result<TotpSetupResponse>> HandleAsync(SetUpTotpCommand command, CancellationToken cancellationToken)
    {
        var stepUp = stepUpPolicy.Check(hasConfirmedMfa: false);
        if (stepUp.IsFailure)
        {
            return Result.Failure<TotpSetupResponse>(stepUp.Error);
        }

        var now = timeProvider.GetUtcNow();
        var caller = await CallerSession.ResolveAsync(currentUser, users, sessions, now, cancellationToken);
        if (caller.IsFailure)
        {
            return Result.Failure<TotpSetupResponse>(caller.Error);
        }

        var (user, session) = caller.Value;
        var userId = user.Id;

        var existing = await mfaMethods.GetTotpAsync(userId, cancellationToken);
        if (existing is { IsConfirmed: true })
        {
            return Result.Failure<TotpSetupResponse>(MfaErrors.AlreadyEnabled);
        }

        var label = command.Label?.Trim() ?? DefaultLabel;
        var secret = Totp.NewSecret();
        try
        {
            var protectedSecret = secretProtector.Protect(secret);
            var method = Stage(existing, userId, session, label, protectedSecret, now);

            if (!await unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.MfaMethodPerUserAndType, cancellationToken))
            {
                // A simultaneous setup inserted the user's row first; nothing of this request was saved. Take that row over.
                existing = await mfaMethods.GetTotpAsync(userId, cancellationToken);
                if (existing is { IsConfirmed: true })
                {
                    return Result.Failure<TotpSetupResponse>(MfaErrors.AlreadyEnabled);
                }

                method = Stage(existing, userId, session, label, protectedSecret, now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return new TotpSetupResponse(method.Id, Base32.Encode(secret), TotpUri.Build(mfaOptions.Value.Issuer, user.Email, secret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Restarts the pending method, or starts and adds a new one, and stages the audit row; nothing is saved.</summary>
    private UserMfaMethod Stage(UserMfaMethod? pending, Guid userId, UserSession session, string label, string protectedSecret, DateTimeOffset now)
    {
        UserMfaMethod method;
        if (pending is null)
        {
            method = UserMfaMethod.StartTotp(userId, label, protectedSecret, now);
            mfaMethods.Add(method);
        }
        else
        {
            pending.RestartSetup(label, protectedSecret, now);
            method = pending;
        }

        // Ids only: the secret, its protected form and the URI never reach an audit row.
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.MfaSetupStarted,
            succeeded: true,
            now,
            userId: userId,
            sessionId: session.Id,
            details: JsonSerializer.Serialize(new { methodId = method.Id })));

        return method;
    }
}
