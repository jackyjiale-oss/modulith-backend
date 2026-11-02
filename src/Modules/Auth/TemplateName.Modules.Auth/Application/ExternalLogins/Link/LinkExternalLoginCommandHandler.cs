using System.Text.Json;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.Link;

/// <summary>
/// Links the identity a provider's proof vouches for to the signed-in user (spec 5.3 and decision D8), in this order:
/// <list type="number">
/// <item>The caller's user and session, as at step-up; otherwise <see cref="UserErrors.NotFound"/> (answered like a request without a
/// valid token).</item>
/// <item>A fresh step-up (<see cref="StepUpPolicy.Check"/>): an account with two-step verification needs a session that proved a code
/// (<c>otp</c> or <c>rcvr</c>), any other account only a recent <c>auth_time</c>: a sign-in or step-up of any kind, because the policy does
/// not look at how it was proved (a fresh social sign-in of a password account without MFA passes too). Checked <b>before</b> anything is
/// verified, so a refused request spends no proof.</item>
/// <item>The provider comes from the catalog, enabled ones only: an unknown and a disabled one are the same
/// <see cref="ExternalLoginErrors.ProviderNotFound"/>. From here on the provider's name is the catalog's, never the route value.</item>
/// <item>The proof is verified (<see cref="IExternalIdentityVerifier"/>, which also spends its nonce).</item>
/// <item>An identity that another user holds is <see cref="ExternalLoginErrors.IdentityTaken"/>; a second identity at a provider the
/// caller already has is <see cref="ExternalLoginErrors.ProviderAlreadyLinked"/>; the caller's own identity again is a success that
/// changes nothing (no audit row, no event, no save).</item>
/// <item>A new link is audited (<c>auth.external_linked</c>) and raises <see cref="Domain.Users.Events.ExternalLoginLinkedDomainEvent"/>.
/// The only change is the new row: the security stamp stays and no session ends (decision D11).</item>
/// </list>
/// <para>
/// The link is saved with the two unique indexes a simultaneous request can win. The loser's save is refused and nothing of it is kept, so
/// it re-reads: the identity now held by another user is <see cref="ExternalLoginErrors.IdentityTaken"/>, held by the caller (a double
/// submit) is the same success as linking it again, another identity now linked at the provider is
/// <see cref="ExternalLoginErrors.ProviderAlreadyLinked"/>, and when no live user holds it (a soft-deleted one does and its index is not
/// filtered) the answer is <see cref="ExternalLoginErrors.IdentityTaken"/>, the safe one; never a 500.
/// </para>
/// A failed link writes no audit row: nothing changed, and the failed proof of a signed-in caller is not a sign-in failure. The proof, the
/// nonce and the subject never reach an audit row, and this handler logs none of them; a save that a unique index refuses is logged by
/// EF Core at Error with SQL Server's message, which quotes the duplicate provider and subject (a Known limit of the Auth page).
/// </summary>
internal sealed class LinkExternalLoginCommandHandler(
    IExternalProviderCatalog catalog,
    IExternalIdentityVerifier verifier,
    IUserRepository users,
    ISessionRepository sessions,
    IMfaMethodRepository mfaMethods,
    StepUpPolicy stepUpPolicy,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<LinkExternalLoginCommand, ExternalLoginResponse>
{
    /// <summary>The indexes a link can lose a race on: the identity's owner, and the caller's one login for the provider.</summary>
    private static readonly string[] RaceIndexes =
    [
        UniqueIndexNames.ExternalLoginIdentity,
        UniqueIndexNames.ExternalLoginProviderPerUser,
    ];

    public async Task<Result<ExternalLoginResponse>> HandleAsync(LinkExternalLoginCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var caller = await CallerSession.ResolveAsync(currentUser, users, sessions, now, cancellationToken);
        if (caller.IsFailure)
        {
            return caller.Error;
        }

        var (user, session) = caller.Value;
        var stepUp = stepUpPolicy.Check(await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken));
        if (stepUp.IsFailure)
        {
            return stepUp.Error;
        }

        var provider = catalog.Find(command.Provider);
        if (provider is null)
        {
            return ExternalLoginErrors.ProviderNotFound;
        }

        var verification = await verifier.VerifyAsync(
            provider,
            ExternalProof.Create(command.IdToken, command.AccessToken, command.Nonce),
            cancellationToken);
        if (verification.IsFailure)
        {
            return verification.Error;
        }

        var identity = verification.Value;
        if (await users.GetByExternalLoginAsync(provider.Name, identity.Subject, cancellationToken) is { } holder && holder.Id != user.Id)
        {
            return ExternalLoginErrors.IdentityTaken;
        }

        var loginsBefore = user.ExternalLogins.Count;
        var linked = user.LinkExternalLogin(provider.Name, identity.Subject, identity.Email, now);
        if (linked.IsFailure)
        {
            return linked.Error;
        }

        if (user.ExternalLogins.Count == loginsBefore)
        {
            // The same identity again: the login is already the caller's, nothing was added and nothing is saved.
            return ExternalLoginResponse.From(linked.Value);
        }

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.ExternalLinked,
            succeeded: true,
            now,
            userId: user.Id,
            sessionId: session.Id,
            details: JsonSerializer.Serialize(new { provider = provider.Name })));

        // One save: the login, the audit row and the outbox row of ExternalLoginLinkedDomainEvent.
        if (!await unitOfWork.SaveChangesUnlessDuplicateAsync(RaceIndexes, cancellationToken))
        {
            return await AfterLostRaceAsync(user.Id, provider.Name, identity.Subject, cancellationToken);
        }

        return ExternalLoginResponse.From(linked.Value);
    }

    /// <summary>A simultaneous request won a unique index (nothing of ours was saved, and the change tracker is empty again): say who holds what now.</summary>
    private async Task<Result<ExternalLoginResponse>> AfterLostRaceAsync(Guid userId, string provider, string subject, CancellationToken cancellationToken)
    {
        if (await users.GetByExternalLoginAsync(provider, subject, cancellationToken) is { } holder)
        {
            if (holder.Id != userId)
            {
                return ExternalLoginErrors.IdentityTaken;
            }

            // The caller's own simultaneous request linked exactly this identity: the same answer as linking it again.
            var own = holder.ExternalLogins.FirstOrDefault(login =>
                login.Provider == provider && string.Equals(login.Subject, subject, StringComparison.Ordinal));
            return own is null ? ExternalLoginErrors.ProviderAlreadyLinked : ExternalLoginResponse.From(own);
        }

        var caller = await users.GetByIdAsync(userId, cancellationToken);
        if (caller is null)
        {
            return UserErrors.NotFound(userId);
        }

        // No live user holds the identity. The caller's own request for another identity won, or a soft-deleted user holds this one.
        return caller.ExternalLogins.Any(login => login.Provider == provider)
            ? ExternalLoginErrors.ProviderAlreadyLinked
            : ExternalLoginErrors.IdentityTaken;
    }
}
