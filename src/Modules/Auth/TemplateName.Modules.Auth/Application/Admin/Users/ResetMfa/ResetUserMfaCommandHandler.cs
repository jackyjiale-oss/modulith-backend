using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users.ResetMfa;

/// <summary>
/// Resets a user's two-step verification, for the user who lost the authenticator and every recovery code (plan decision D17). In this
/// order: a caller cannot reset their own (<see cref="UserErrors.CannotResetOwnMfa"/>: the self-service route with a step-up is for that);
/// an unknown or soft-deleted user is <see cref="UserErrors.NotFound"/>; only an active SuperAdmin can reset a SuperAdmin; a user with no
/// method at all is a no-op (nothing changed, no audit row, nothing saved). Otherwise the method, pending or confirmed, and every
/// recovery code are removed. When the method was <b>confirmed</b>, <see cref="MfaChangeEffects"/> also rotates the security stamp (which
/// ends the pending challenges and the remembered devices), revokes every active session with <see cref="SessionRevokedReason.AdminRevoked"/>
/// (the actor is not the target, so none is kept) and raises <see cref="MfaChange.ResetByAdmin"/>; a <b>pending</b> setup alone protects
/// nothing and is just removed, as when its owner cancels it. <c>auth.admin_mfa_reset</c> is audited and everything is saved once with no
/// cancellation token, so a reset is stored even when the client goes away; the user's <c>RowVersion</c> and the method's concurrency
/// tokens guard the rows.
/// </summary>
internal sealed class ResetUserMfaCommandHandler(
    IUserRepository users,
    IMfaMethodRepository mfaMethods,
    IRecoveryCodeRepository recoveryCodes,
    SuperAdminRules superAdminRules,
    MfaChangeEffects mfaChangeEffects,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<ResetUserMfaCommand>
{
    public async Task<Result> HandleAsync(ResetUserMfaCommand command, CancellationToken cancellationToken)
    {
        if (command.UserId == command.ActorId)
        {
            return Result.Failure(UserErrors.CannotResetOwnMfa);
        }

        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserErrors.NotFound(command.UserId));
        }

        var scope = await superAdminRules.GetScopeAsync(command.ActorId, cancellationToken);
        var allowed = scope.EnsureCanManage(user);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        // TOTP is the only method type, so at most one exists (the unique index allows one per type).
        var method = await mfaMethods.GetTotpAsync(user.Id, cancellationToken);
        if (method is null)
        {
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();
        var wasConfirmed = method.IsConfirmed;
        mfaMethods.Remove(method);
        foreach (var code in await recoveryCodes.ListByUserAsync(user.Id, cancellationToken))
        {
            recoveryCodes.Remove(code);
        }

        if (wasConfirmed)
        {
            await mfaChangeEffects.ApplyAsync(user, MfaChange.ResetByAdmin, keepSessionId: null, SessionRevokedReason.AdminRevoked, now, cancellationToken);
        }

        auditWriter.Record(AdminAudit.Succeeded(AuthAuditEvents.AdminMfaReset, user.Id, command.ActorId, now));

        // One save: the method, the codes, the stamp, the sessions, the audit row and the outbox row of MfaChangedDomainEvent.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Success();
    }
}
