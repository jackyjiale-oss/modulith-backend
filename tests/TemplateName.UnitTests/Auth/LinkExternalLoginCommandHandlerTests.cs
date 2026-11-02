using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.ExternalLogins;
using TemplateName.Modules.Auth.Application.ExternalLogins.Link;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class LinkExternalLoginCommandHandlerTests
{
    private const string Subject = "google-subject-0001";
    private const string IdToken = "header.payload.signature-of-the-id-token";
    private const string Nonce = "nonce-0123456789abcdef";
    private const string ProviderEmail = "alice.work@example.com";

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedInAt = Now.AddMinutes(-3);

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

    private static readonly string[] DuplicateIndexes = [UniqueIndexNames.ExternalLoginIdentity, UniqueIndexNames.ExternalLoginProviderPerUser];

    private readonly IExternalProviderCatalog _catalog = Substitute.For<IExternalProviderCatalog>();
    private readonly IExternalIdentityVerifier _verifier = Substitute.For<IExternalIdentityVerifier>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IMfaMethodRepository _mfaMethods = Substitute.For<IMfaMethodRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<ExternalProof> _proofs = [];
    private readonly User _user;
    private readonly UserSession _current;
    private readonly LinkExternalLoginCommandHandler _sut;

    public LinkExternalLoginCommandHandlerTests()
    {
        _user = User.Register("alice@example.com", "Alice", "en", "stored-hash", SignedInAt.AddDays(-1)).Value;
        _user.ConfirmEmail(SignedInAt.AddDays(-1));
        _user.ClearDomainEvents();
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);

        _current = StartSession(_user);
        _sessions.GetByIdAsync(_current.Id, Arg.Any<CancellationToken>()).Returns(_current);

        _currentUser.UserId.Returns(_user.Id);
        _currentUser.SessionId.Returns(_current.Id);
        _currentUser.AuthTime.Returns(SignedInAt);
        _currentUser.AuthMethods.Returns(["pwd"]);

        _catalog.Find(Arg.Is<string>(name => name != null && name.Trim().Equals("google", StringComparison.OrdinalIgnoreCase))).Returns(Google);
        _verifier.VerifyAsync(Arg.Any<ExternalProvider>(), Arg.Do<ExternalProof>(_proofs.Add), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new VerifiedExternalIdentity("google", Subject, ProviderEmail, HasUsableEmail: true, Name: "Alice Example")));
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _unitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(true);

        var time = new FakeTimeProvider(Now);
        _sut = new LinkExternalLoginCommandHandler(
            _catalog,
            _verifier,
            _users,
            _sessions,
            _mfaMethods,
            new StepUpPolicy(_currentUser, Options.Create(new StepUpOptions()), time),
            _currentUser,
            _auditWriter,
            _unitOfWork,
            time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Links_the_proven_identity_audits_it_and_raises_the_event_without_touching_a_session()
    {
        var other = StartSession(_user);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([_current, other]);
        var stamp = _user.SecurityStamp;

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        var login = _user.ExternalLogins.ShouldHaveSingleItem();
        login.Provider.ShouldBe("google");
        login.Subject.ShouldBe(Subject);
        login.Email.ShouldBe(ProviderEmail);
        login.LinkedAt.ShouldBe(Now);
        login.LastUsedAt.ShouldBeNull();
        result.Value.ShouldBe(new ExternalLoginResponse(login.Id, "google", ProviderEmail, Now, null));
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new ExternalLoginLinkedDomainEvent(_user.Id, "google"));

        // Linking adds a way in and ends nothing: no new stamp, no revoked session.
        _user.SecurityStamp.ShouldBe(stamp);
        _current.RevokedAt.ShouldBeNull();
        other.RevokedAt.ShouldBeNull();
        await _sessions.DidNotReceiveWithAnyArgs().GetActiveByUserAsync(default, default, Ct);

        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.ExternalLinked);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_current.Id);
        audit.Details.ShouldBe("""{"provider":"google"}""");

        // One save carries the login, the audit row and the event; both unique indexes a simultaneous request can win are answered.
        await _unitOfWork.Received(1).SaveChangesUnlessDuplicateAsync(
            Arg.Is<IReadOnlyCollection<string>>(names => names.Order().SequenceEqual(DuplicateIndexes.Order())),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_proof_goes_to_the_verifier_for_the_catalog_provider_with_blank_values_as_absent()
    {
        var result = await _sut.HandleAsync(new LinkExternalLoginCommand(" GOOGLE ", IdToken, "   ", Nonce), Ct);

        result.IsSuccess.ShouldBeTrue();
        var proof = _proofs.ShouldHaveSingleItem();
        proof.IdToken.ShouldBe(IdToken);
        proof.AccessToken.ShouldBeNull();
        proof.Nonce.ShouldBe(Nonce);
        await _verifier.Received(1).VerifyAsync(Google, Arg.Any<ExternalProof>(), Arg.Any<CancellationToken>());
        _user.ExternalLogins.ShouldHaveSingleItem().Provider.ShouldBe("google");
    }

    [Theory]
    [InlineData(ProviderEmail)]
    [InlineData(null)]
    public async Task A_provider_email_that_is_not_usable_is_still_kept_for_display_only(string? email)
    {
        // Linking an identity is the caller's own act, so the email only has to be shown, never trusted to find an account.
        GivenIdentity(new VerifiedExternalIdentity("google", Subject, email, HasUsableEmail: false, Name: null));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Email.ShouldBe(email);
        _user.ExternalLogins.ShouldHaveSingleItem().Email.ShouldBe(email);
    }

    [Fact]
    public async Task A_user_with_no_password_who_stepped_up_with_a_login_links_a_second_provider()
    {
        var social = SocialOnlyUser();
        GivenCaller(social);
        _currentUser.AuthMethods.Returns(["fed"]);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        social.ExternalLogins.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("no-auth-time")]
    public async Task Link_without_a_fresh_step_up_is_step_up_required_and_spends_no_proof(string scenario)
    {
        if (scenario == "stale")
        {
            _currentUser.AuthTime.Returns(Now - TimeSpan.FromMinutes(10));
        }
        else
        {
            _currentUser.AuthTime.Returns((DateTimeOffset?)null);
        }

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
        await _verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, Ct);
        await AssertNothingChangedAsync();
    }

    [Theory]
    [InlineData("pwd", false)]
    [InlineData("fed", false)]
    [InlineData("pwd mfa", false)]
    [InlineData("otp", true)]
    [InlineData("rcvr", true)]
    [InlineData("fed otp mfa", true)]
    public async Task An_account_with_two_step_verification_needs_a_session_that_proved_a_code(string amr, bool allowed)
    {
        _mfaMethods.HasConfirmedAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(true);
        _currentUser.AuthMethods.Returns(amr.Split(' '));

        var result = await _sut.HandleAsync(Command(), Ct);

        if (allowed)
        {
            result.IsSuccess.ShouldBeTrue();
            return;
        }

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
        await _verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, Ct);
        await AssertNothingChangedAsync();
    }

    [Theory]
    [InlineData("myspace")]
    [InlineData("apple")]
    public async Task An_unknown_or_disabled_provider_is_not_found_and_nothing_is_verified(string provider)
    {
        // The catalog finds enabled providers only, so a disabled one is unknown to it.
        var result = await _sut.HandleAsync(Command() with { Provider = provider }, Ct);

        result.Error.ShouldBe(ExternalLoginErrors.ProviderNotFound);
        await _verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, Ct);
        await AssertNothingChangedAsync();
    }

    [Theory]
    [InlineData("auth.invalid_external_token")]
    [InlineData("auth.external_provider_unavailable")]
    public async Task A_proof_that_fails_verification_links_nothing(string code)
    {
        var error = code == ExternalLoginErrors.InvalidToken.Code ? ExternalLoginErrors.InvalidToken : ExternalLoginErrors.ProviderUnavailable;
        _verifier.VerifyAsync(Google, Arg.Any<ExternalProof>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<VerifiedExternalIdentity>(error));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(error);
        await _users.DidNotReceiveWithAnyArgs().GetByExternalLoginAsync(default!, default!, Ct);
        await AssertNothingChangedAsync();
    }

    [Fact]
    public async Task An_identity_that_belongs_to_another_user_is_identity_taken()
    {
        var owner = SocialOnlyUser();
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns(owner);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.IdentityTaken);
        _user.ExternalLogins.ShouldBeEmpty();
        owner.ExternalLogins.ShouldHaveSingleItem();
        await AssertNothingChangedAsync();
    }

    [Fact]
    public async Task A_second_identity_for_the_same_provider_is_provider_already_linked()
    {
        _user.LinkExternalLogin("google", "another-subject", ProviderEmail, Now.AddDays(-3));
        _user.ClearDomainEvents();

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.ProviderAlreadyLinked);
        _user.ExternalLogins.ShouldHaveSingleItem().Subject.ShouldBe("another-subject");
        await AssertNothingChangedAsync();
    }

    [Fact]
    public async Task The_same_identity_again_is_ok_and_changes_nothing()
    {
        var existing = _user.LinkExternalLogin("google", Subject, "first@example.com", Now.AddDays(-3)).Value;
        _user.ClearDomainEvents();
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns(_user);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new ExternalLoginResponse(existing.Id, "google", "first@example.com", Now.AddDays(-3), null));
        _user.ExternalLogins.ShouldHaveSingleItem().ShouldBeSameAs(existing);
        await AssertNothingChangedAsync();
    }

    [Fact]
    public async Task Losing_the_race_for_the_identity_to_another_user_is_identity_taken_not_a_500()
    {
        var winner = SocialOnlyUser();
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns((User?)null, winner);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.IdentityTaken);
    }

    [Fact]
    public async Task Losing_the_race_to_the_callers_own_simultaneous_link_of_the_same_identity_is_ok()
    {
        // The re-read finds the caller holding exactly this identity: the same answer as linking it again.
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns((User?)null, _user);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Provider.ShouldBe("google");
    }

    [Fact]
    public async Task Losing_the_race_to_the_callers_simultaneous_link_of_another_identity_is_provider_already_linked()
    {
        var reread = User.Register("alice@example.com", "Alice", "en", "stored-hash", SignedInAt.AddDays(-1)).Value;
        reread.LinkExternalLogin("google", "another-subject", null, Now);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user, reread);
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns((User?)null);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.ProviderAlreadyLinked);
    }

    [Fact]
    public async Task Losing_the_race_to_a_soft_deleted_holder_fails_closed_as_identity_taken()
    {
        // No live user holds the identity (a soft-deleted one does, and its index is not filtered), and the caller, read again after the
        // refused save emptied the change tracker, has no login.
        var reread = User.Register("alice@example.com", "Alice", "en", "stored-hash", SignedInAt.AddDays(-1)).Value;
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user, reread);
        _users.GetByExternalLoginAsync("google", Subject, Arg.Any<CancellationToken>()).Returns((User?)null);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.IdentityTaken);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("no-session")]
    [InlineData("unknown-user")]
    [InlineData("suspended")]
    [InlineData("unknown-session")]
    [InlineData("revoked-session")]
    [InlineData("changed-stamp")]
    public async Task Caller_without_an_active_session_of_their_own_is_unauthorized_and_nothing_is_verified(string scenario)
    {
        switch (scenario)
        {
            case "anonymous":
                _currentUser.UserId.Returns((Guid?)null);
                break;
            case "no-session":
                _currentUser.SessionId.Returns((Guid?)null);
                break;
            case "unknown-user":
                _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((User?)null);
                break;
            case "suspended":
                _user.Suspend(Now);
                break;
            case "unknown-session":
                _sessions.GetByIdAsync(_current.Id, Arg.Any<CancellationToken>()).Returns((UserSession?)null);
                break;
            case "revoked-session":
                _current.Revoke(SessionRevokedReason.Logout, Now);
                break;
            default:
                _user.ChangeMfa(MfaChange.Enabled, Now);
                break;
        }

        _user.ClearDomainEvents();

        var result = await _sut.HandleAsync(Command(), Ct);

        // The user-not-found error the endpoint answers like a request without a valid token.
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Code.ShouldBe(UserErrors.NotFound(_user.Id).Code);
        await _verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, Ct);
        await AssertNothingChangedAsync();
    }

    [Fact]
    public void The_command_and_the_response_print_only_their_type_name()
    {
        new LinkExternalLoginCommand("google", IdToken, null, Nonce).ToString().ShouldBe(nameof(LinkExternalLoginCommand));
        new ExternalLoginResponse(Guid.NewGuid(), "google", ProviderEmail, Now, null).ToString().ShouldBe(nameof(ExternalLoginResponse));
    }

    private static LinkExternalLoginCommand Command() => new("google", IdToken, null, Nonce);

    private static User SocialOnlyUser()
    {
        var user = User.RegisterExternal("bob@example.com", "Bob", "en", "apple", "apple-subject-0001", "bob@example.com", SignedInAt.AddDays(-1)).Value;
        user.ClearDomainEvents();
        return user;
    }

    private static UserSession StartSession(User user)
    {
        var (session, _) = UserSession.Start(
            user.Id,
            AuthenticationMethods.Password,
            "Phone",
            userAgent: null,
            ipAddress: null,
            user.SecurityStamp,
            [.. Enumerable.Range(1, 32).Select(value => (byte)value)],
            TimeSpan.FromDays(14),
            TimeSpan.FromDays(90),
            SignedInAt);
        return session;
    }

    private void GivenIdentity(VerifiedExternalIdentity identity)
        => _verifier.VerifyAsync(Arg.Any<ExternalProvider>(), Arg.Any<ExternalProof>(), Arg.Any<CancellationToken>()).Returns(Result.Success(identity));

    private void GivenCaller(User user)
    {
        var session = StartSession(user);
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _sessions.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _currentUser.UserId.Returns(user.Id);
        _currentUser.SessionId.Returns(session.Id);
    }

    /// <summary>A refusal saves nothing, writes no audit row and raises no event.</summary>
    private async Task AssertNothingChangedAsync()
    {
        _auditEntries.ShouldBeEmpty();
        _user.DomainEvents.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessDuplicateAsync(default(IReadOnlyCollection<string>)!, Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().TrySaveChangesAsync(Ct);
    }
}
