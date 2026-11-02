using System.Text.Json;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.Unlink;

/// <summary>
/// Removes one of the caller's external logins (spec 5.5 and decision D11), in this order:
/// <list type="number">
/// <item>The caller's user and session, as at step-up; otherwise <see cref="Domain.Users.UserErrors.NotFound"/> (answered like a request
/// without a valid token).</item>
/// <item>A fresh step-up (<see cref="StepUpPolicy.Check"/>; an account with two-step verification needs a session that proved a code),
/// before the login is looked up, so a session that has not stepped up learns nothing about which ids are its own.</item>
/// <item><see cref="Domain.Users.User.UnlinkExternalLogin"/> finds the login among the caller's own, so an unknown id and another user's
/// are the same <see cref="Domain.Users.ExternalLoginErrors.LoginNotFound"/>; it refuses the last way to sign in
/// (<see cref="Domain.Users.ExternalLoginErrors.LastLoginMethod"/>); otherwise it removes the row, rotates the security stamp and raises
/// <see cref="Domain.Users.Events.ExternalLoginUnlinkedDomainEvent"/>.</item>
/// <item>Every <b>other</b> active session of the caller is revoked with <see cref="SessionRevokedReason.ExternalLoginChanged"/>; the
/// current one adopts the new stamp and stays signed in. The pending sign-in challenges and the remembered devices end with the old stamp.</item>
/// <item><c>auth.external_unlinked</c> is audited with the provider and the number of sessions revoked.</item>
/// </list>
/// One save commits all of it; a simultaneous change of the same user is a 409 <c>concurrency.conflict</c> that saved nothing. The
/// provider is never looked up in the catalog, so a login of a switched-off provider can be removed.
/// </summary>
internal sealed class UnlinkExternalLoginCommandHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IMfaMethodRepository mfaMethods,
    StepUpPolicy stepUpPolicy,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<UnlinkExternalLoginCommand>
{
    public async Task<Result> HandleAsync(UnlinkExternalLoginCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var caller = await CallerSession.ResolveAsync(currentUser, users, sessions, now, cancellationToken);
        if (caller.IsFailure)
        {
            return Result.Failure(caller.Error);
        }

        var (user, current) = caller.Value;
        var stepUp = stepUpPolicy.Check(await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken));
        if (stepUp.IsFailure)
        {
            return stepUp;
        }

        // Read before the unlink removes it: the audit row names the provider.
        var provider = user.ExternalLogins.FirstOrDefault(login => login.Id == command.Id)?.Provider;
        var unlinked = user.UnlinkExternalLogin(command.Id, now);
        if (unlinked.IsFailure)
        {
            return unlinked;
        }

        // The new stamp ends every session at its next refresh; the current one adopts it and stays, the others are revoked now.
        var revokedSessionCount = 0;
        foreach (var session in await sessions.GetActiveByUserAsync(user.Id, now, cancellationToken))
        {
            if (session.Id == current.Id)
            {
                session.AdoptSecurityStamp(user.SecurityStamp);
                continue;
            }

            session.Revoke(SessionRevokedReason.ExternalLoginChanged, now);
            revokedSessionCount++;
        }

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.ExternalUnlinked,
            succeeded: true,
            now,
            userId: user.Id,
            sessionId: current.Id,
            details: JsonSerializer.Serialize(new { provider, revokedSessionCount })));

        // One save: the removed login, the stamp, the sessions, the audit row and the outbox row of ExternalLoginUnlinkedDomainEvent.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
