using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Users.ForcePasswordReset;

/// <summary>
/// Forces a password reset: a reset code is issued as by forgot-password (<see cref="PasswordResetLinkIssuer"/>, which emails the link
/// through the outbox and invalidates pending ones), every active session is revoked with <see cref="SessionRevokedReason.AdminRevoked"/>,
/// an audit entry is written and everything is saved together. The token reaches nobody but the outbox, encrypted. The current password
/// keeps working until the user resets it. A suspended account is refused with <see cref="UserErrors.AccountInactive"/>, because the
/// reset would refuse its link anyway; only a SuperAdmin can force a reset on a SuperAdmin.
/// </summary>
internal sealed class ForcePasswordResetCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    SuperAdminRules superAdminRules,
    PasswordResetLinkIssuer linkIssuer,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<ForcePasswordResetCommand>
{
    public async Task<Result> HandleAsync(ForcePasswordResetCommand command, CancellationToken cancellationToken)
    {
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

        if (user.Status == UserStatus.Suspended)
        {
            return Result.Failure(UserErrors.AccountInactive);
        }

        var now = timeProvider.GetUtcNow();
        await linkIssuer.IssueAsync(user, now, cancellationToken);
        foreach (var session in await sessions.GetActiveByUserAsync(user.Id, now, cancellationToken))
        {
            session.Revoke(SessionRevokedReason.AdminRevoked, now);
        }

        auditWriter.Record(AdminAudit.Succeeded(AuthAuditEvents.AdminPasswordResetForced, user.Id, command.ActorId, now));

        // The code, the revocations, the audit entry and the outbox row of the issued event commit together, even if the client goes away.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Success();
    }
}
