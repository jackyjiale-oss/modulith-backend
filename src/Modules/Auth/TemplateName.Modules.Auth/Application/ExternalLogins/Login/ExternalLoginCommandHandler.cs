using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.Authentication.Login;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.Login;

/// <summary>
/// Signs a user in, or up, with a provider's proof (spec 5.1 and 5.4), in this order:
/// <list type="number">
/// <item>The provider comes from the catalog; an unknown and a disabled one are the same <see cref="ExternalLoginErrors.ProviderNotFound"/>.
/// From here on the provider's name is the catalog's (trimmed, lower-case), never the route value.</item>
/// <item>The proof is verified (<see cref="IExternalIdentityVerifier"/>, which also spends its nonce).</item>
/// <item>A linked identity is found by <c>(Provider, Subject)</c> only, never by email, and goes to the session.</item>
/// <item>An identity that is not linked needs a usable email (the provider's rule and its <c>TrustEmail</c>), else
/// <see cref="ExternalLoginErrors.EmailUnverified"/>; no account is looked up before that, so no other answer can tell whether one exists.
/// With one, the account with that (normalized) email is linked when its email is confirmed and it has no confirmed MFA method (strict
/// linking, 5.3, and Ruling R14), else <see cref="ExternalLoginErrors.LinkRequired"/>; without an account, one is registered when <c>AllowSignUp</c> is on, else
/// <see cref="ExternalLoginErrors.SignUpDisabled"/>. An email a soft-deleted account still holds stays reserved (Ruling R6).</item>
/// <item>The session (5.4): a suspended account is <see cref="UserErrors.AccountInactive"/> and a locked one
/// <see cref="UserErrors.InvalidCredentials"/>, neither counted as a failure; an account with a confirmed MFA method gets a challenge whose
/// first factor is <c>fed</c> (a trusted device never stands in for it, so no device token is read); otherwise
/// <see cref="SessionStarter"/> starts the session with <c>amr</c> <c>fed</c> and the login's <c>LastUsedAt</c> is set.</item>
/// </list>
/// <para>
/// A link or a registration is saved on its own, before the session, so the unique indexes on <c>(Provider, Subject)</c>,
/// <c>(UserId, Provider)</c> and the email decide a race: the loser's save is refused, nothing of it is kept, and it re-reads, signing in
/// the user that now holds the identity, else answering <see cref="ExternalLoginErrors.LinkRequired"/> when an account now has the email,
/// else (a soft-deleted user holds the identity) <see cref="UserErrors.InvalidCredentials"/>; never a 500. The session's own save is the
/// password login's: a concurrency conflict is the generic 401.
/// </para>
/// <para>
/// Every outcome is counted (<c>auth.external_logins</c>, tagged with the catalog's provider name only), and every success and every
/// refusal the handler itself decides is audited (a failure as <c>auth.external_login_failed</c> with its error code as the reason, or
/// <c>locked</c> for a locked account, as the password login writes). The exception is a session save or a challenge save that loses a
/// concurrency race: it answers the generic 401 and writes no failed-audit row, as the password login does. The proof, the nonce and the
/// subject never reach an audit row or a tag, and this handler logs none of them. But a save that a unique index refuses (a lost race, and
/// every attempt that reaches a save for an identity a soft-deleted user still holds) is logged by EF Core at Error with SQL Server's message, which
/// quotes the duplicate key: the provider and subject, or the normalized email (a Known limit of the Auth page).
/// </para>
/// </summary>
internal sealed class ExternalLoginCommandHandler(
    IExternalProviderCatalog catalog,
    IExternalIdentityVerifier verifier,
    IUserRepository users,
    IRoleRepository roles,
    IMfaMethodRepository mfaMethods,
    IMfaChallengeRepository challenges,
    ISecureTokenService tokenService,
    SessionStarter sessionStarter,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    IAuthMetrics metrics,
    IOptions<MfaOptions> mfaOptions,
    TimeProvider timeProvider) : ICommandHandler<ExternalLoginCommand, LoginResult>
{
    private const string DefaultDeviceName = "Unknown";

    /// <summary>The indexes a link or a registration can lose a race on; each is answered by a re-read.</summary>
    private static readonly string[] RaceIndexes =
    [
        UniqueIndexNames.ExternalLoginIdentity,
        UniqueIndexNames.ExternalLoginProviderPerUser,
        UniqueIndexNames.UserEmail,
    ];

    public async Task<Result<LoginResult>> HandleAsync(ExternalLoginCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var provider = catalog.Find(command.Provider);
        if (provider is null)
        {
            return await RejectAsync(provider: null, user: null, ExternalLoginErrors.ProviderNotFound, ExternalLoginOutcome.ProviderNotFound, now, cancellationToken);
        }

        // The validator counts a blank value as absent, so the verifier (which tests for null or empty) must see it as absent too.
        var proof = ExternalProof.Create(command.IdToken, command.AccessToken, command.Nonce);
        var verification = await verifier.VerifyAsync(provider, proof, cancellationToken);
        if (verification.IsFailure)
        {
            var outcome = verification.Error == ExternalLoginErrors.ProviderUnavailable
                ? ExternalLoginOutcome.ProviderUnavailable
                : ExternalLoginOutcome.InvalidToken;
            return await RejectAsync(provider, user: null, verification.Error, outcome, now, cancellationToken);
        }

        var identity = verification.Value;
        var attempt = new Attempt(provider, identity.Subject, command.DeviceName ?? DefaultDeviceName, now);

        // 3. A linked identity: found by (Provider, Subject) only.
        if (await users.GetByExternalLoginAsync(provider.Name, identity.Subject, cancellationToken) is { } linked)
        {
            return await StartAsync(linked, attempt, cancellationToken);
        }

        // 4. Not linked: nothing about accounts is looked at before the provider has vouched for a usable email.
        if (!identity.HasUsableEmail || string.IsNullOrWhiteSpace(identity.Email))
        {
            return await RejectAsync(provider, user: null, ExternalLoginErrors.EmailUnverified, ExternalLoginOutcome.EmailUnverified, now, cancellationToken);
        }

        var email = identity.Email.Trim();
        var normalizedEmail = User.NormalizeEmail(email);
        var existing = await users.GetByNormalizedEmailAsync(normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            return await LinkAsync(existing, email, normalizedEmail, attempt, cancellationToken);
        }

        if (!catalog.AllowSignUp)
        {
            return await RejectAsync(provider, user: null, ExternalLoginErrors.SignUpDisabled, ExternalLoginOutcome.SignUpDisabled, now, cancellationToken);
        }

        // Ruling R6: the address of a soft-deleted account stays reserved, so it is refused like a locked account.
        if (await users.ExistsByNormalizedEmailIncludingDeletedAsync(normalizedEmail, cancellationToken))
        {
            return await RejectAsync(provider, user: null, UserErrors.InvalidCredentials, ExternalLoginOutcome.InvalidCredentials, now, cancellationToken);
        }

        return await RegisterAsync(command, identity, email, normalizedEmail, attempt, cancellationToken);
    }

    /// <summary>
    /// The automatic link of strict linking (5.3): only to an account whose email is confirmed and that has no other identity at this
    /// provider, and only when the account may sign in at all, so a suspended or locked account never gains a way in. An account with a
    /// confirmed MFA method is never linked here (Ruling R14): the link would be saved before its second factor is answered.
    /// </summary>
    private async Task<Result<LoginResult>> LinkAsync(
        User user,
        string email,
        string normalizedEmail,
        Attempt attempt,
        CancellationToken cancellationToken)
    {
        if (!user.EmailConfirmed || user.ExternalLogins.Any(login => login.Provider == attempt.Provider.Name))
        {
            return await RejectAsync(attempt.Provider, user, ExternalLoginErrors.LinkRequired, ExternalLoginOutcome.LinkRequired, attempt.Now, cancellationToken);
        }

        if (await RejectIfBlockedAsync(user, attempt, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        // Ruling R14: a link saved here would attach the identity before the second factor is answered (the challenge comes after the
        // save), so an account with two-step verification is linked only by hand, from a session that proved a code.
        if (await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken))
        {
            return await RejectAsync(attempt.Provider, user, ExternalLoginErrors.LinkRequired, ExternalLoginOutcome.LinkRequired, attempt.Now, cancellationToken);
        }

        var linked = user.LinkExternalLogin(attempt.Provider.Name, attempt.Subject, email, attempt.Now);
        if (linked.IsFailure)
        {
            return await RejectAsync(attempt.Provider, user, ExternalLoginErrors.LinkRequired, ExternalLoginOutcome.LinkRequired, attempt.Now, cancellationToken);
        }

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.ExternalLinked,
            succeeded: true,
            attempt.Now,
            userId: user.Id,
            details: ProviderDetails(attempt.Provider)));

        if (!await unitOfWork.SaveChangesUnlessDuplicateAsync(RaceIndexes, cancellationToken))
        {
            return await ContinueAfterLostRaceAsync(normalizedEmail, attempt, cancellationToken);
        }

        return await StartAsync(user, attempt, cancellationToken);
    }

    /// <summary>
    /// Registers the account of a first sign-in (5.6): active, no password, the email confirmed (the provider vouched for it), role
    /// <c>User</c>, the identity linked. The display name is the body's, else the token's (cut first, Ruling R9), else the email's local
    /// part; trimmed and at most 200 characters. The locale is the body's, else the request's language.
    /// </summary>
    private async Task<Result<LoginResult>> RegisterAsync(
        ExternalLoginCommand command,
        VerifiedExternalIdentity identity,
        string email,
        string normalizedEmail,
        Attempt attempt,
        CancellationToken cancellationToken)
    {
        var userRole = await roles.GetByNormalizedNameAsync(Role.NormalizeName(SystemRoles.User), cancellationToken)
            ?? throw new InvalidOperationException(
                $"The system role '{SystemRoles.User}' does not exist, so no account can be registered. Seed the Auth module (SeedAuthModuleAsync) first.");

        var registration = User.RegisterExternal(
            email,
            DisplayNameFor(command.DisplayName, identity.Name, email),
            LocaleFor(command.Locale),
            attempt.Provider.Name,
            attempt.Subject,
            email,
            attempt.Now);
        if (registration.IsFailure)
        {
            return await RejectAsync(attempt.Provider, user: null, registration.Error, ExternalLoginOutcome.InvalidCredentials, attempt.Now, cancellationToken);
        }

        var user = registration.Value;
        user.AssignRole(userRole.Id, assignedBy: null, attempt.Now);
        users.Add(user);

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.ExternalRegistered,
            succeeded: true,
            attempt.Now,
            userId: user.Id,
            details: ProviderDetails(attempt.Provider)));

        if (!await unitOfWork.SaveChangesUnlessDuplicateAsync(RaceIndexes, cancellationToken))
        {
            return await ContinueAfterLostRaceAsync(normalizedEmail, attempt, cancellationToken);
        }

        return await StartAsync(user, attempt, cancellationToken);
    }

    /// <summary>
    /// A simultaneous request won a unique index (nothing of ours was saved, and the change tracker is empty again): re-read, and sign in
    /// the user that now holds the identity; else answer as for an account with the email that cannot be linked; else no live user holds
    /// either (a soft-deleted one holds the identity, Ruling R6), which is the generic 401.
    /// </summary>
    private async Task<Result<LoginResult>> ContinueAfterLostRaceAsync(string normalizedEmail, Attempt attempt, CancellationToken cancellationToken)
    {
        if (await users.GetByExternalLoginAsync(attempt.Provider.Name, attempt.Subject, cancellationToken) is { } holder)
        {
            return await StartAsync(holder, attempt, cancellationToken);
        }

        if (await users.GetByNormalizedEmailAsync(normalizedEmail, cancellationToken) is { } owner)
        {
            return await RejectAsync(attempt.Provider, owner, ExternalLoginErrors.LinkRequired, ExternalLoginOutcome.LinkRequired, attempt.Now, cancellationToken);
        }

        return await RejectAsync(attempt.Provider, user: null, UserErrors.InvalidCredentials, ExternalLoginOutcome.InvalidCredentials, attempt.Now, cancellationToken);
    }

    /// <summary>Starting the session (5.4) for a user that holds the identity: the checks, then the challenge or the session.</summary>
    private async Task<Result<LoginResult>> StartAsync(User user, Attempt attempt, CancellationToken cancellationToken)
    {
        // Exactly the identity the provider vouched for: the database compares subjects without regard to case, this does not.
        var login = user.ExternalLogins.FirstOrDefault(candidate =>
            candidate.Provider == attempt.Provider.Name && string.Equals(candidate.Subject, attempt.Subject, StringComparison.Ordinal));
        if (login is null)
        {
            return await RejectAsync(attempt.Provider, user, UserErrors.InvalidCredentials, ExternalLoginOutcome.InvalidCredentials, attempt.Now, cancellationToken);
        }

        if (await RejectIfBlockedAsync(user, attempt, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        // The unconfirmed-email refusal of a password login (User.EnsureCanSignIn) is not applied: it asks a password user to prove the
        // address, and here a provider has proved the identity. Suspension, the other half of that check, was refused above.
        if (await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken))
        {
            return await ChallengeAsync(user, attempt, cancellationToken);
        }

        login.NoteUsed(attempt.Now);
        var started = await sessionStarter.StartAsync(
            user,
            AuthenticationMethods.Federated,
            attempt.DeviceName,
            viaTrustedDevice: false,
            attempt.Now,
            cancellationToken);

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.ExternalLoginSucceeded,
            succeeded: true,
            attempt.Now,
            userId: user.Id,
            sessionId: started.Session.Id,
            details: ProviderDetails(attempt.Provider)));

        // Another request changed this user between our read and this save: nothing was saved; the generic 401, as for a password login.
        if (!await unitOfWork.TrySaveChangesAsync(cancellationToken))
        {
            metrics.RecordExternalLogin(attempt.Provider, ExternalLoginOutcome.InvalidCredentials);
            return Result.Failure<LoginResult>(UserErrors.InvalidCredentials);
        }

        metrics.RecordExternalLogin(attempt.Provider, ExternalLoginOutcome.Succeeded);

        return LoginResult.Signed(started.Tokens);
    }

    /// <summary>
    /// The challenge of an account with two-step verification, first factor <c>fed</c>: <c>POST /api/v1/auth/mfa/verify</c> answers it and
    /// records <c>fed otp mfa</c>. No session and no recorded sign-in until a code is verified.
    /// </summary>
    private async Task<Result<LoginResult>> ChallengeAsync(User user, Attempt attempt, CancellationToken cancellationToken)
    {
        var token = tokenService.Generate();
        var challenge = MfaChallenge.Issue(
            user.Id,
            token.Hash,
            user.SecurityStamp,
            attempt.DeviceName,
            AuthenticationMethods.Federated,
            mfaOptions.Value.ChallengeLifetime,
            attempt.Now);
        challenges.Add(challenge);

        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.MfaChallengeIssued,
            succeeded: true,
            attempt.Now,
            userId: user.Id,
            details: ProviderDetails(attempt.Provider)));

        if (!await unitOfWork.TrySaveChangesAsync(cancellationToken))
        {
            metrics.RecordExternalLogin(attempt.Provider, ExternalLoginOutcome.InvalidCredentials);
            return Result.Failure<LoginResult>(UserErrors.InvalidCredentials);
        }

        metrics.RecordExternalLogin(attempt.Provider, ExternalLoginOutcome.MfaRequired);

        return LoginResult.Challenge(token.Value, challenge.ExpiresAt);
    }

    /// <summary>A suspended account (403) or a locked one (the generic 401, decision D10); neither touches the failure count. Null when neither.</summary>
    private async Task<Result<LoginResult>?> RejectIfBlockedAsync(User user, Attempt attempt, CancellationToken cancellationToken)
    {
        if (user.Status == UserStatus.Suspended)
        {
            return await RejectAsync(attempt.Provider, user, UserErrors.AccountInactive, ExternalLoginOutcome.Inactive, attempt.Now, cancellationToken);
        }

        if (user.IsLockedOut(attempt.Now))
        {
            // The answer is the generic 401, the audit reason is the password login's (Ruling R12).
            return await RejectAsync(
                attempt.Provider,
                user,
                UserErrors.InvalidCredentials,
                LoginCommandHandler.LockedFailureReason,
                ExternalLoginOutcome.Locked,
                attempt.Now,
                cancellationToken);
        }

        return null;
    }

    /// <summary>Audits and counts a failure and answers <paramref name="error"/>, whose code is the audit reason.</summary>
    private Task<Result<LoginResult>> RejectAsync(
        ExternalProvider? provider,
        User? user,
        Error error,
        ExternalLoginOutcome outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => RejectAsync(provider, user, error, error.Code, outcome, now, cancellationToken);

    /// <summary>
    /// Audits and counts a failure and answers <paramref name="error"/>, with <paramref name="failureReason"/> as the audit reason (a
    /// locked account answers the generic error but audits <c>locked</c>). Only the audit row is saved: every caller reaches this before
    /// it changes anything, or after a refused save emptied the change tracker.
    /// </summary>
    private async Task<Result<LoginResult>> RejectAsync(
        ExternalProvider? provider,
        User? user,
        Error error,
        string failureReason,
        ExternalLoginOutcome outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.ExternalLoginFailed,
            succeeded: false,
            now,
            userId: user?.Id,
            failureReason: failureReason,
            details: provider is null ? null : ProviderDetails(provider)));

        await unitOfWork.SaveChangesAsync(cancellationToken);
        metrics.RecordExternalLogin(provider, outcome);

        return Result.Failure<LoginResult>(error);
    }

    private static string ProviderDetails(ExternalProvider provider) => JsonSerializer.Serialize(new { provider = provider.Name });

    /// <summary>
    /// The first of the body's name, the token's name and the email's local part that is not blank, trimmed and at most
    /// <see cref="User.MaxDisplayNameLength"/> characters. Each is cut before it is trimmed, so an 8 KB token name costs nothing more.
    /// </summary>
    private static string DisplayNameFor(string? bodyName, string? tokenName, string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        var localPart = at > 0 ? email[..at] : email;

        return Usable(bodyName) ?? Usable(tokenName) ?? Usable(localPart) ?? Cut(email).Trim();
    }

    private static string? Usable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = Cut(value.TrimStart()).TrimEnd();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>At most <see cref="User.MaxDisplayNameLength"/> characters, never ending in half of a surrogate pair.</summary>
    private static string Cut(string value)
    {
        if (value.Length <= User.MaxDisplayNameLength)
        {
            return value;
        }

        var length = char.IsHighSurrogate(value[User.MaxDisplayNameLength - 1]) ? User.MaxDisplayNameLength - 1 : User.MaxDisplayNameLength;
        return value[..length];
    }

    /// <summary>The body's locale in its canonical spelling (the validator accepted it as a predefined culture), else the request's language.</summary>
    private static string LocaleFor(string? locale)
        => string.IsNullOrWhiteSpace(locale)
            ? CultureInfo.CurrentUICulture.Name
            : CultureInfo.GetCultureInfo(locale, predefinedOnly: true).Name;

    /// <summary>What every step needs to know about this attempt: the catalog's provider, the vouched subject, the device and the instant.</summary>
    private sealed record Attempt(ExternalProvider Provider, string Subject, string DeviceName, DateTimeOffset Now)
    {
        /// <summary>The provider only: the generated <c>ToString</c> would print the subject.</summary>
        public override string ToString() => $"{nameof(Attempt)} {{ Provider = {Provider.Name} }}";
    }
}
