using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.ExternalLogins.Login;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Sessions.Events;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class ExternalLoginCommandHandlerTests
{
    private const string Subject = "google-subject-0001";
    private const string IdToken = "header.payload.signature-of-the-id-token";
    private const string Nonce = "nonce-0123456789abcdef";
    private const string RefreshTokenValue = "refresh-token-value-of-forty-three-chars-xx";
    private const string AccessTokenValue = "header.payload.signature";
    private const string Email = "alice@example.com";

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] TokenHash = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private static readonly ExternalProvider Google = new(
        "google",
        "Google",
        ExternalProviderKind.Oidc,
        "https://accounts.google.com",
        ["https://accounts.google.com"],
        ["client.apps.example"],
        ExternalEmailRule.EmailVerifiedClaim,
        TrustEmail: true,
        TenantId: null,
        AppId: null,
        AppSecret: null);

    private static readonly string[] DuplicateIndexes = [UniqueIndexNames.ExternalLoginIdentity, UniqueIndexNames.ExternalLoginProviderPerUser, UniqueIndexNames.UserEmail];

    private readonly IExternalProviderCatalog _catalog = Substitute.For<IExternalProviderCatalog>();
    private readonly IExternalIdentityVerifier _verifier = Substitute.For<IExternalIdentityVerifier>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRoleRepository _roles = Substitute.For<IRoleRepository>();
    private readonly IMfaMethodRepository _mfaMethods = Substitute.For<IMfaMethodRepository>();
    private readonly IMfaChallengeRepository _challenges = Substitute.For<IMfaChallengeRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly ISecureTokenService _tokenService = Substitute.For<ISecureTokenService>();
    private readonly IAccessTokenIssuer _accessTokenIssuer = Substitute.For<IAccessTokenIssuer>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IClientContext _clientContext = Substitute.For<IClientContext>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuthMetrics _metrics = Substitute.For<IAuthMetrics>();
    private readonly Role _userRole = Role.CreateSystem(SystemRoles.User, "Every registered user", Now.AddDays(-30));
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<UserSession> _addedSessions = [];
    private readonly List<MfaChallenge> _addedChallenges = [];
    private readonly List<User> _addedUsers = [];
    private readonly List<AccessTokenRequest> _tokenRequests = [];
    private readonly List<ExternalProof> _proofs = [];
    private readonly ExternalLoginCommandHandler _sut;

    public ExternalLoginCommandHandlerTests()
    {
        _catalog.Find(Arg.Is<string>(name => name != null && name.Trim().Equals("google", StringComparison.OrdinalIgnoreCase))).Returns(Google);
        _catalog.AllowSignUp.Returns(true);
        GivenIdentity(Identity());
        _tokenService.Generate().Returns(new GeneratedToken(RefreshTokenValue, TokenHash));
        _accessTokenIssuer.Issue(Arg.Do<AccessTokenRequest>(_tokenRequests.Add)).Returns(new AccessToken(AccessTokenValue, Now.AddMinutes(10)));
        _clientContext.IpAddress.Returns("203.0.113.7");
        _clientContext.UserAgent.Returns("UnitTests/1.0");
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _sessions.Add(Arg.Do<UserSession>(_addedSessions.Add));
        _sessions.GetDeviceFamiliarityAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(DeviceFamiliarity.NoStoredSession);
        _challenges.Add(Arg.Do<MfaChallenge>(_addedChallenges.Add));
        _users.Add(Arg.Do<User>(_addedUsers.Add));
        _roles.GetByNormalizedNameAsync(Role.NormalizeName(SystemRoles.User), Arg.Any<CancellationToken>()).Returns(_userRole);
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(true);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        _sut = CreateHandler(_catalog);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Linked_identity_signs_in_with_fed_and_notes_the_login_used()
    {
        var user = GivenLinkedUser();

        var result = await _sut.HandleAsync(Command() with { DeviceName = "Alice's phone" }, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.MfaRequired.ShouldBeFalse();
        result.Value.AccessToken.ShouldBe(AccessTokenValue);
        result.Value.RefreshToken.ShouldBe(RefreshTokenValue);
        var session = _addedSessions.ShouldHaveSingleItem();
        session.UserId.ShouldBe(user.Id);
        session.AuthMethods.ShouldBe(AuthenticationMethods.Federated);
        session.DeviceName.ShouldBe("Alice's phone");
        result.Value.SessionId.ShouldBe(session.Id);
        _tokenRequests.ShouldHaveSingleItem().AuthMethods.ShouldBe("fed");
        user.LastLoginAt.ShouldBe(Now);
        user.ExternalLogins.ShouldHaveSingleItem().LastUsedAt.ShouldBe(Now);

        // The proof went to the verifier as posted, for the catalog's provider.
        var proof = _proofs.ShouldHaveSingleItem();
        proof.IdToken.ShouldBe(IdToken);
        proof.AccessToken.ShouldBeNull();
        proof.Nonce.ShouldBe(Nonce);
        await _verifier.Received(1).VerifyAsync(Google, Arg.Any<ExternalProof>(), Arg.Any<CancellationToken>());

        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.ExternalLoginSucceeded);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(user.Id);
        audit.SessionId.ShouldBe(session.Id);
        audit.Details.ShouldBe("""{"provider":"google"}""");
        await _unitOfWork.Received(1).TrySaveChangesAsync(Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        await _users.DidNotReceiveWithAnyArgs().GetByNormalizedEmailAsync(default!, Ct);
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.Succeeded);
    }

    [Fact]
    public async Task Provider_of_the_route_is_matched_without_case_and_the_catalog_name_is_used()
    {
        GivenLinkedUser();

        var result = await _sut.HandleAsync(Command() with { Provider = " GOOGLE " }, Ct);

        result.IsSuccess.ShouldBeTrue();
        await _users.Received(1).GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.Succeeded);
    }

    [Fact]
    public async Task Device_name_defaults_to_Unknown()
    {
        GivenLinkedUser();

        await _sut.HandleAsync(Command(), Ct);

        _addedSessions.ShouldHaveSingleItem().DeviceName.ShouldBe("Unknown");
    }

    [Fact]
    public async Task Unknown_and_disabled_providers_answer_alike_and_nothing_is_verified()
    {
        var catalog = RealCatalog(("Google:Enabled", "true"), ("Google:ClientIds:0", "client.apps.example"), ("Apple:Enabled", "false"));
        var sut = CreateHandler(catalog);

        var unknown = await sut.HandleAsync(Command() with { Provider = "myspace" }, Ct);
        var disabled = await sut.HandleAsync(Command() with { Provider = "apple" }, Ct);

        unknown.Error.ShouldBe(ExternalLoginErrors.ProviderNotFound);
        disabled.Error.ShouldBe(unknown.Error);
        await _verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, Ct);
        await _users.DidNotReceiveWithAnyArgs().GetByExternalLoginAsync(default!, default!, Ct);
        _auditEntries.Count.ShouldBe(2);
        _auditEntries.ShouldAllBe(entry => entry.EventType == AuthAuditEvents.ExternalLoginFailed
            && !entry.Succeeded
            && entry.FailureReason == "auth.external_provider_not_found"
            && entry.UserId == null
            && entry.Details == null
            && entry.AttemptedIdentifier == null);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());

        // The route value is never a tag: an unknown provider is counted without one.
        _metrics.Received(2).RecordExternalLogin(null, ExternalLoginOutcome.ProviderNotFound);
    }

    [Theory]
    [InlineData("auth.invalid_external_token")]
    [InlineData("auth.external_provider_unavailable")]
    public async Task A_proof_that_fails_verification_is_refused_and_audited(string code)
    {
        var error = code == ExternalLoginErrors.InvalidToken.Code ? ExternalLoginErrors.InvalidToken : ExternalLoginErrors.ProviderUnavailable;
        _verifier.VerifyAsync(Google, Arg.Any<ExternalProof>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<VerifiedExternalIdentity>(error));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(error);
        await _users.DidNotReceiveWithAnyArgs().GetByExternalLoginAsync(default!, default!, Ct);
        await _users.DidNotReceiveWithAnyArgs().GetByNormalizedEmailAsync(default!, Ct);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.ExternalLoginFailed);
        audit.FailureReason.ShouldBe(code);
        audit.Details.ShouldBe("""{"provider":"google"}""");
        _metrics.Received(1).RecordExternalLogin(
            Google,
            error == ExternalLoginErrors.InvalidToken ? ExternalLoginOutcome.InvalidToken : ExternalLoginOutcome.ProviderUnavailable);
    }

    [Theory]
    [InlineData(Email, false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public async Task Unlinked_identity_without_a_usable_email_is_EmailUnverified_and_looks_up_no_account(string? email, bool hasUsableEmail)
    {
        // An account with the address exists: the answer must not tell, because the caller proved no address.
        GivenPasswordUser();
        GivenIdentity(Identity() with { Email = email, HasUsableEmail = hasUsableEmail });

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.EmailUnverified);
        await _users.DidNotReceiveWithAnyArgs().GetByNormalizedEmailAsync(default!, Ct);
        _addedUsers.ShouldBeEmpty();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.FailureReason.ShouldBe("auth.external_email_unverified");
        audit.UserId.ShouldBeNull();
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.EmailUnverified);
    }

    [Fact]
    public async Task Email_of_a_confirmed_account_links_the_identity_and_signs_in()
    {
        var user = GivenPasswordUser();

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        var login = user.ExternalLogins.ShouldHaveSingleItem();
        login.Provider.ShouldBe("google");
        login.Subject.ShouldBe(Subject);
        login.Email.ShouldBe(Email);
        login.LastUsedAt.ShouldBe(Now);
        user.DomainEvents.OfType<ExternalLoginLinkedDomainEvent>().ShouldHaveSingleItem().ShouldBe(new ExternalLoginLinkedDomainEvent(user.Id, "google"));
        _addedUsers.ShouldBeEmpty();
        _addedSessions.ShouldHaveSingleItem().AuthMethods.ShouldBe("fed");
        _auditEntries.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.ExternalLinked, AuthAuditEvents.ExternalLoginSucceeded]);
        _auditEntries[0].UserId.ShouldBe(user.Id);
        _auditEntries[0].Details.ShouldBe("""{"provider":"google"}""");

        // The link is saved on its own first (a lost race is refused by an index), then the session as at every sign-in.
        await _unitOfWork.Received(1).SaveChangesUnlessDuplicateAsync(
            Arg.Is<IReadOnlyCollection<string>>(names => names.Order().SequenceEqual(DuplicateIndexes.Order())),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).TrySaveChangesAsync(Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.Succeeded);
    }

    [Fact]
    public async Task Provider_email_with_other_case_and_trailing_space_links_to_the_existing_account()
    {
        var user = GivenPasswordUser();
        GivenIdentity(Identity() with { Email = "Alice@Example.com " });

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        await _users.Received(1).GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>());
        var login = user.ExternalLogins.ShouldHaveSingleItem();
        login.Subject.ShouldBe(Subject);
        login.Email.ShouldBe("Alice@Example.com");
        _addedUsers.ShouldBeEmpty();
        await _users.DidNotReceiveWithAnyArgs().ExistsByNormalizedEmailIncludingDeletedAsync(default!, Ct);
    }

    [Fact]
    public async Task Provider_email_with_other_case_and_surrounding_space_signs_up_with_the_trimmed_address()
    {
        GivenIdentity(Identity() with { Email = " Alice@Example.com " });

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        var user = _addedUsers.ShouldHaveSingleItem();
        user.Email.ShouldBe("Alice@Example.com");
        user.NormalizedEmail.ShouldBe("ALICE@EXAMPLE.COM");
        user.ExternalLogins.ShouldHaveSingleItem().Email.ShouldBe("Alice@Example.com");
        await _users.Received(1).ExistsByNormalizedEmailIncludingDeletedAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Email_of_an_unconfirmed_account_is_LinkRequired_and_links_nothing()
    {
        var user = GivenPasswordUser(confirmed: false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.LinkRequired);
        user.ExternalLogins.ShouldBeEmpty();
        user.DomainEvents.ShouldBeEmpty();
        _addedSessions.ShouldBeEmpty();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.ExternalLoginFailed);
        audit.FailureReason.ShouldBe("auth.external_link_required");
        audit.UserId.ShouldBe(user.Id);
        await _unitOfWork.DidNotReceive().SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.LinkRequired);
    }

    [Fact]
    public async Task Email_of_an_account_with_another_identity_at_the_provider_is_LinkRequired()
    {
        var user = GivenPasswordUser();
        user.LinkExternalLogin("google", "another-subject", Email, Now.AddDays(-2));
        user.ClearDomainEvents();

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.LinkRequired);
        user.ExternalLogins.ShouldHaveSingleItem().Subject.ShouldBe("another-subject");
        _addedSessions.ShouldBeEmpty();
        _auditEntries.ShouldHaveSingleItem().FailureReason.ShouldBe("auth.external_link_required");
    }

    [Fact]
    public async Task No_account_with_sign_up_on_registers_a_confirmed_passwordless_user_and_signs_in()
    {
        var result = await _sut.HandleAsync(Command() with { DisplayName = "  Alice Body  ", Locale = "ms" }, Ct);

        result.IsSuccess.ShouldBeTrue();
        var user = _addedUsers.ShouldHaveSingleItem();
        user.Email.ShouldBe(Email);
        user.NormalizedEmail.ShouldBe("ALICE@EXAMPLE.COM");
        user.EmailConfirmed.ShouldBeTrue();
        user.HasPassword.ShouldBeFalse();
        user.Status.ShouldBe(UserStatus.Active);
        user.DisplayName.ShouldBe("Alice Body");
        user.Locale.ShouldBe("ms");
        user.Roles.ShouldHaveSingleItem().RoleId.ShouldBe(_userRole.Id);
        var login = user.ExternalLogins.ShouldHaveSingleItem();
        login.Provider.ShouldBe("google");
        login.Subject.ShouldBe(Subject);
        login.LastUsedAt.ShouldBe(Now);
        user.DomainEvents.OfType<UserRegisteredDomainEvent>().ShouldHaveSingleItem();
        user.DomainEvents.OfType<EmailConfirmedDomainEvent>().ShouldHaveSingleItem();
        user.DomainEvents.OfType<ExternalLoginLinkedDomainEvent>().ShouldBeEmpty();
        await _users.Received(1).ExistsByNormalizedEmailIncludingDeletedAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>());
        _addedSessions.ShouldHaveSingleItem().AuthMethods.ShouldBe("fed");
        _auditEntries.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.ExternalRegistered, AuthAuditEvents.ExternalLoginSucceeded]);
        _auditEntries[0].UserId.ShouldBe(user.Id);
        _auditEntries[0].Details.ShouldBe("""{"provider":"google"}""");
        await _unitOfWork.Received(1).SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.Succeeded);
    }

    [Fact]
    public async Task Sign_up_off_is_SignUpDisabled()
    {
        _catalog.AllowSignUp.Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.SignUpDisabled);
        _addedUsers.ShouldBeEmpty();
        _addedSessions.ShouldBeEmpty();
        _auditEntries.ShouldHaveSingleItem().FailureReason.ShouldBe("auth.external_sign_up_disabled");
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.SignUpDisabled);
    }

    [Fact]
    public async Task Email_a_deleted_account_still_holds_is_refused_like_a_locked_account()
    {
        _users.ExistsByNormalizedEmailIncludingDeletedAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        _addedUsers.ShouldBeEmpty();
        _addedSessions.ShouldBeEmpty();
        _auditEntries.ShouldHaveSingleItem().FailureReason.ShouldBe("auth.invalid_credentials");
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.InvalidCredentials);
    }

    [Fact]
    public async Task Suspended_account_is_account_inactive_and_counts_no_failure()
    {
        var user = GivenLinkedUser();
        user.Suspend(Now.AddDays(-1));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.AccountInactive);
        user.AccessFailedCount.ShouldBe(0);
        user.LastLoginAt.ShouldBeNull();
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _addedSessions.ShouldBeEmpty();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.FailureReason.ShouldBe("auth.account_inactive");
        audit.UserId.ShouldBe(user.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.Inactive);
    }

    [Fact]
    public async Task Locked_account_is_invalid_credentials_and_counts_no_failure()
    {
        var user = GivenLinkedUser();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            user.RecordFailedSignIn(Now, maxFailedAttempts: 5, LockoutDuration);
        }

        user.ClearDomainEvents();

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        user.AccessFailedCount.ShouldBe(5);
        user.LockoutEnd.ShouldBe(Now + LockoutDuration);
        user.LastLoginAt.ShouldBeNull();
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _addedSessions.ShouldBeEmpty();

        // The HTTP answer is the generic 401, but the audit row says why, as the password login's does (Ruling R12).
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.FailureReason.ShouldBe("locked");
        audit.UserId.ShouldBe(user.Id);
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.Locked);
    }

    [Theory]
    [InlineData(" ", "facebook-access-token-value", "   ", null, "facebook-access-token-value", null)]
    [InlineData("", "facebook-access-token-value", "", null, "facebook-access-token-value", null)]
    [InlineData(IdToken, " ", Nonce, IdToken, null, Nonce)]
    [InlineData(IdToken, "\t", "  ", IdToken, null, null)]
    [InlineData(null, "facebook-access-token-value", null, null, "facebook-access-token-value", null)]
    public async Task A_blank_proof_value_reaches_the_verifier_as_absent(
        string? idToken,
        string? accessToken,
        string? nonce,
        string? expectedIdToken,
        string? expectedAccessToken,
        string? expectedNonce)
    {
        GivenLinkedUser();

        await _sut.HandleAsync(Command() with { IdToken = idToken, AccessToken = accessToken, Nonce = nonce }, Ct);

        var proof = _proofs.ShouldHaveSingleItem();
        proof.IdToken.ShouldBe(expectedIdToken);
        proof.AccessToken.ShouldBe(expectedAccessToken);
        proof.Nonce.ShouldBe(expectedNonce);
    }

    [Fact]
    public async Task Suspended_or_locked_account_found_by_email_is_refused_before_anything_is_linked()
    {
        var suspended = GivenPasswordUser();
        suspended.Suspend(Now.AddDays(-1));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.AccountInactive);
        suspended.ExternalLogins.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());

        var locked = GivenPasswordUser();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            locked.RecordFailedSignIn(Now, maxFailedAttempts: 5, LockoutDuration);
        }

        result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        locked.ExternalLogins.ShouldBeEmpty();
        locked.AccessFailedCount.ShouldBe(5);
        await _unitOfWork.DidNotReceive().SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        _addedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Federated_user_with_an_unconfirmed_email_is_not_blocked_by_the_email_check()
    {
        // A login linked from settings to an account whose address was never confirmed: the provider proved the identity, and the
        // password login's unconfirmed-email refusal is about proving the address, which a linked identity does not need.
        var user = GivenPasswordUser(confirmed: false);
        user.LinkExternalLogin("google", Subject, Email, Now.AddDays(-1));
        user.ClearDomainEvents();
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Account_with_a_confirmed_mfa_method_gets_a_fed_challenge_and_no_session()
    {
        var user = GivenLinkedUser();
        _mfaMethods.HasConfirmedAsync(user.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HandleAsync(Command() with { DeviceName = "Alice's phone" }, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.MfaRequired.ShouldBeTrue();
        result.Value.ChallengeToken.ShouldBe(RefreshTokenValue);
        result.Value.ChallengeExpiresAt.ShouldBe(Now + new MfaOptions().ChallengeLifetime);
        result.Value.AccessToken.ShouldBeNull();
        var challenge = _addedChallenges.ShouldHaveSingleItem();
        challenge.FirstFactor.ShouldBe(AuthenticationMethods.Federated);
        challenge.UserId.ShouldBe(user.Id);
        challenge.DeviceName.ShouldBe("Alice's phone");
        challenge.SecurityStamp.ShouldBe(user.SecurityStamp);
        challenge.TokenHash.ShouldBe(TokenHash);
        _addedSessions.ShouldBeEmpty();
        user.LastLoginAt.ShouldBeNull();

        // No trusted device is looked for: there is no device token to hash, and the handler has no device repository at all.
        _tokenService.DidNotReceiveWithAnyArgs().Hash(default!);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.MfaChallengeIssued);
        audit.UserId.ShouldBe(user.Id);
        audit.Details.ShouldBe("""{"provider":"google"}""");
        await _unitOfWork.Received(1).TrySaveChangesAsync(Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.MfaRequired);
    }

    [Fact]
    public async Task Sign_in_from_a_new_device_raises_the_new_device_notice()
    {
        var user = GivenLinkedUser();
        user.RecordSuccessfulSignIn(Now.AddDays(-3));
        _sessions.GetDeviceFamiliarityAsync(user.Id, "Unknown", "UnitTests/1.0", Arg.Any<CancellationToken>()).Returns(DeviceFamiliarity.New);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        var session = _addedSessions.ShouldHaveSingleItem();
        session.DomainEvents.ShouldHaveSingleItem().ShouldBe(new NewDeviceSignedInDomainEvent(user.Id, session.Id, "203.0.113.7"));
    }

    [Fact]
    public async Task Lost_race_on_a_first_sign_in_rereads_and_signs_in_the_winner()
    {
        var winner = LinkedUser();
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns((User?)null, winner);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        _addedUsers.ShouldHaveSingleItem().ShouldNotBe(winner);
        _addedSessions.ShouldHaveSingleItem().UserId.ShouldBe(winner.Id);
        winner.ExternalLogins.ShouldHaveSingleItem().LastUsedAt.ShouldBe(Now);
        var success = _auditEntries[^1];
        success.EventType.ShouldBe(AuthAuditEvents.ExternalLoginSucceeded);
        success.UserId.ShouldBe(winner.Id);
        await _users.Received(2).GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).TrySaveChangesAsync(Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.Succeeded);
    }

    [Fact]
    public async Task Lost_race_on_a_link_rereads_and_signs_in_the_user_that_holds_the_identity()
    {
        var user = GivenPasswordUser();
        var linked = LinkedUser();
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns((User?)null, linked);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        user.ExternalLogins.ShouldHaveSingleItem();
        _addedSessions.ShouldHaveSingleItem().UserId.ShouldBe(linked.Id);
    }

    [Fact]
    public async Task Lost_race_that_finds_only_an_account_with_the_email_ends_on_link_required()
    {
        // Another provider's first sign-in created the account a moment ago: the identity is not linked, the email is taken.
        var other = PasswordUser();
        _users.GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns((User?)null, other);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.LinkRequired);
        _addedSessions.ShouldBeEmpty();
        var failure = _auditEntries[^1];
        failure.EventType.ShouldBe(AuthAuditEvents.ExternalLoginFailed);
        failure.FailureReason.ShouldBe("auth.external_link_required");
        failure.UserId.ShouldBe(other.Id);
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.LinkRequired);
    }

    [Fact]
    public async Task Duplicate_refusal_whose_reread_finds_no_live_user_is_invalid_credentials_not_a_500()
    {
        // The identity belongs to a soft-deleted user: the index refuses it, and no live user holds it or the email.
        _unitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        _addedSessions.ShouldBeEmpty();
        var failure = _auditEntries[^1];
        failure.EventType.ShouldBe(AuthAuditEvents.ExternalLoginFailed);
        failure.FailureReason.ShouldBe("auth.invalid_credentials");
        failure.UserId.ShouldBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.InvalidCredentials);
    }

    [Fact]
    public async Task Lost_session_save_is_invalid_credentials()
    {
        GivenLinkedUser();
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);

        // As for a password login, a save that loses a concurrency race is the generic 401 and writes no failed-audit row.
        _auditEntries.ShouldNotContain(entry => entry.EventType == AuthAuditEvents.ExternalLoginFailed);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordExternalLogin(Google, ExternalLoginOutcome.InvalidCredentials);
    }

    [Fact]
    public async Task Identity_whose_stored_subject_differs_only_in_case_is_refused()
    {
        // The database compares subjects without regard to case; the handler insists on the exact subject the provider vouched for.
        var user = GivenPasswordUser();
        user.LinkExternalLogin("google", Subject.ToUpperInvariant(), Email, Now.AddDays(-1));
        user.ClearDomainEvents();
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        _addedSessions.ShouldBeEmpty();
        _auditEntries.ShouldHaveSingleItem().FailureReason.ShouldBe("auth.invalid_credentials");
    }

    [Theory]
    [InlineData("  Alice Body  ", "Alice Token", "Alice Body")]
    [InlineData(null, "  Alice Token ", "Alice Token")]
    [InlineData("   ", "Alice Token", "Alice Token")]
    [InlineData(null, null, "alice")]
    [InlineData(" ", "  ", "alice")]
    public async Task Display_name_is_the_body_then_the_token_name_then_the_email_local_part(string? body, string? tokenName, string expected)
    {
        GivenIdentity(Identity() with { Name = tokenName });

        await _sut.HandleAsync(Command() with { DisplayName = body }, Ct);

        _addedUsers.ShouldHaveSingleItem().DisplayName.ShouldBe(expected);
    }

    [Fact]
    public async Task A_long_token_name_is_cut_to_200_characters()
    {
        GivenIdentity(Identity() with { Name = new string('n', 8000) });

        await _sut.HandleAsync(Command(), Ct);

        _addedUsers.ShouldHaveSingleItem().DisplayName.ShouldBe(new string('n', 200));
    }

    [Fact]
    public async Task A_missing_name_falls_back_to_the_email_local_part_cut_to_200()
    {
        var localPart = new string('a', 230);
        GivenIdentity(Identity() with { Email = $"{localPart}@example.com", Name = null });

        await _sut.HandleAsync(Command(), Ct);

        _addedUsers.ShouldHaveSingleItem().DisplayName.ShouldBe(new string('a', 200));
    }

    [Fact]
    public async Task An_absent_locale_takes_the_request_language_and_a_given_one_is_stored_canonically()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-Hans");
            await _sut.HandleAsync(Command() with { Locale = null }, Ct);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }

        _addedUsers.ShouldHaveSingleItem().Locale.ShouldBe("zh-Hans");

        _addedUsers.Clear();
        await _sut.HandleAsync(Command() with { Locale = "ZH-hans" }, Ct);

        _addedUsers.ShouldHaveSingleItem().Locale.ShouldBe("zh-Hans");
    }

    [Fact]
    public async Task No_proof_reaches_an_audit_row_and_the_command_prints_only_its_type_name()
    {
        var command = Command() with { AccessToken = "facebook-access-token-value", DisplayName = "Alice" };
        command.ToString().ShouldBe(nameof(ExternalLoginCommand));
        $"{command}".ShouldNotContain(IdToken);

        await _sut.HandleAsync(Command(), Ct);
        _verifier.VerifyAsync(Google, Arg.Any<ExternalProof>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<VerifiedExternalIdentity>(ExternalLoginErrors.InvalidToken));
        await _sut.HandleAsync(Command(), Ct);

        _auditEntries.Count.ShouldBe(3);
        foreach (var entry in _auditEntries)
        {
            foreach (var text in new[] { entry.Details, entry.FailureReason, entry.AttemptedIdentifier })
            {
                (text ?? string.Empty).ShouldNotContain(IdToken);
                (text ?? string.Empty).ShouldNotContain(Nonce);
                (text ?? string.Empty).ShouldNotContain(Subject);
            }
        }
    }

    private static ExternalLoginCommand Command() => new("google", IdToken, null, Nonce, null, null, null);

    private static VerifiedExternalIdentity Identity() => new("google", Subject, Email, HasUsableEmail: true, Name: "Alice Example");

    private static IExternalProviderCatalog RealCatalog(params (string Key, string Value)[] settings)
    {
        var values = settings.ToDictionary(setting => $"Auth:External:Providers:{setting.Key}", setting => (string?)setting.Value);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        AuthModule.AddExternalLogin(services, configuration);
        return services.BuildServiceProvider().GetRequiredService<IExternalProviderCatalog>();
    }

    private static User PasswordUser(bool confirmed = true)
    {
        var user = User.Register(Email, "Alice", "en", "stored-hash", Now.AddDays(-10)).Value;
        if (confirmed)
        {
            user.ConfirmEmail(Now.AddDays(-10));
        }

        user.ClearDomainEvents();
        return user;
    }

    private static User LinkedUser()
    {
        var user = User.RegisterExternal(Email, "Alice", "en", "google", Subject, Email, Now.AddDays(-10)).Value;
        user.ClearDomainEvents();
        return user;
    }

    private ExternalLoginCommandHandler CreateHandler(IExternalProviderCatalog catalog)
    {
        var sessionStarter = new SessionStarter(_sessions, _tokenService, _accessTokenIssuer, _clientContext, Options.Create(new RefreshTokenOptions()));
        return new ExternalLoginCommandHandler(
            catalog,
            _verifier,
            _users,
            _roles,
            _mfaMethods,
            _challenges,
            _tokenService,
            sessionStarter,
            _auditWriter,
            _unitOfWork,
            _metrics,
            Options.Create(new MfaOptions()),
            new FakeTimeProvider(Now));
    }

    private void GivenIdentity(VerifiedExternalIdentity identity)
        => _verifier.VerifyAsync(Arg.Any<ExternalProvider>(), Arg.Do<ExternalProof>(_proofs.Add), Arg.Any<CancellationToken>())
            .Returns(Result.Success(identity));

    private User GivenPasswordUser(bool confirmed = true)
    {
        var user = PasswordUser(confirmed);
        _users.GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }

    private User GivenLinkedUser()
    {
        var user = LinkedUser();
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }
}
