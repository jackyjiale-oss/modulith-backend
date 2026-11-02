using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.ExternalLogins.Unlink;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class UnlinkExternalLoginCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedInAt = Now.AddMinutes(-3);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IMfaMethodRepository _mfaMethods = Substitute.For<IMfaMethodRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly UnlinkExternalLoginCommandHandler _sut;
    private User _user;
    private UserSession _current;

    public UnlinkExternalLoginCommandHandlerTests()
    {
        _user = PasswordUser();
        _current = UseCaller(_user);

        _currentUser.AuthTime.Returns(SignedInAt);
        _currentUser.AuthMethods.Returns(["pwd"]);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));

        var time = new FakeTimeProvider(Now);
        _sut = new UnlinkExternalLoginCommandHandler(
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
    public async Task Unlinking_removes_the_login_rotates_the_stamp_and_ends_the_other_sessions_only()
    {
        var login = _user.ExternalLogins.ShouldHaveSingleItem();
        var other = StartSession(_user);
        var third = StartSession(_user);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([other, _current, third]);
        var oldStamp = _user.SecurityStamp;

        var result = await _sut.HandleAsync(new UnlinkExternalLoginCommand(login.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        _user.ExternalLogins.ShouldBeEmpty();
        _user.SecurityStamp.ShouldNotBe(oldStamp);
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new ExternalLoginUnlinkedDomainEvent(_user.Id, "google"));

        // The caller's other sessions end with the new reason; the current one adopts the new stamp and stays signed in.
        other.RevokedReason.ShouldBe(SessionRevokedReason.ExternalLoginChanged);
        other.RevokedAt.ShouldBe(Now);
        third.RevokedReason.ShouldBe(SessionRevokedReason.ExternalLoginChanged);
        _current.RevokedAt.ShouldBeNull();
        _current.SecurityStamp.ShouldBe(_user.SecurityStamp);

        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.ExternalUnlinked);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_current.Id);
        audit.Details.ShouldBe("""{"provider":"google","revokedSessionCount":2}""");

        // One save commits the removal, the stamp, the sessions, the audit row and the outbox row of the event.
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().TrySaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Unlinking_with_no_other_session_revokes_none_and_audits_a_count_of_zero()
    {
        var login = _user.ExternalLogins.ShouldHaveSingleItem();
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([_current]);

        var result = await _sut.HandleAsync(new UnlinkExternalLoginCommand(login.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        _current.RevokedAt.ShouldBeNull();
        _auditEntries.ShouldHaveSingleItem().Details.ShouldBe("""{"provider":"google","revokedSessionCount":0}""");
    }

    [Fact]
    public async Task A_login_of_a_switched_off_provider_can_be_unlinked()
    {
        // The handler never asks the catalog: a provider that is disabled, or no longer configured at all, can still be removed.
        var login = _user.LinkExternalLogin("retired-provider", "retired-subject", null, Now.AddDays(-9)).Value;
        _user.ClearDomainEvents();
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([_current]);

        var result = await _sut.HandleAsync(new UnlinkExternalLoginCommand(login.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        _user.ExternalLogins.Select(candidate => candidate.Provider).ShouldBe(["google"]);
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new ExternalLoginUnlinkedDomainEvent(_user.Id, "retired-provider"));
    }

    [Fact]
    public async Task The_last_login_of_a_user_with_another_login_and_no_password_can_be_unlinked()
    {
        var social = SocialOnlyUser();
        social.LinkExternalLogin("apple", "apple-subject-0001", null, Now.AddDays(-2));
        var session = UseCaller(social);
        _sessions.GetActiveByUserAsync(social.Id, Now, Arg.Any<CancellationToken>()).Returns([session]);
        var google = social.ExternalLogins.Single(candidate => candidate.Provider == "google");

        var result = await _sut.HandleAsync(new UnlinkExternalLoginCommand(google.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        social.ExternalLogins.ShouldHaveSingleItem().Provider.ShouldBe("apple");
    }

    [Fact]
    public async Task The_only_way_to_sign_in_cannot_be_unlinked()
    {
        var social = SocialOnlyUser();
        var session = UseCaller(social);
        var other = StartSession(social);
        _sessions.GetActiveByUserAsync(social.Id, Now, Arg.Any<CancellationToken>()).Returns([session, other]);
        var login = social.ExternalLogins.ShouldHaveSingleItem();
        var oldStamp = social.SecurityStamp;

        var result = await _sut.HandleAsync(new UnlinkExternalLoginCommand(login.Id), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.LastLoginMethod);
        social.ExternalLogins.ShouldHaveSingleItem();
        social.SecurityStamp.ShouldBe(oldStamp);
        social.DomainEvents.ShouldBeEmpty();
        other.RevokedAt.ShouldBeNull();
        await AssertNothingSavedAsync();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("another-users")]
    public async Task An_unknown_login_and_another_users_login_are_alike_not_found(string scenario)
    {
        var id = Guid.NewGuid();
        if (scenario == "another-users")
        {
            var stranger = SocialOnlyUser();
            id = stranger.ExternalLogins.ShouldHaveSingleItem().Id;
        }

        var oldStamp = _user.SecurityStamp;

        var result = await _sut.HandleAsync(new UnlinkExternalLoginCommand(id), Ct);

        result.Error.ShouldBe(ExternalLoginErrors.LoginNotFound);
        _user.ExternalLogins.ShouldHaveSingleItem();
        _user.SecurityStamp.ShouldBe(oldStamp);
        _user.DomainEvents.ShouldBeEmpty();
        await AssertNothingSavedAsync();
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("no-auth-time")]
    public async Task Unlinking_without_a_fresh_step_up_is_step_up_required(string scenario)
    {
        var login = _user.ExternalLogins.ShouldHaveSingleItem();
        if (scenario == "stale")
        {
            _currentUser.AuthTime.Returns(Now - TimeSpan.FromMinutes(10));
        }
        else
        {
            _currentUser.AuthTime.Returns((DateTimeOffset?)null);
        }

        var result = await _sut.HandleAsync(new UnlinkExternalLoginCommand(login.Id), Ct);

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
        _user.ExternalLogins.ShouldHaveSingleItem();
        _user.DomainEvents.ShouldBeEmpty();
        await AssertNothingSavedAsync();
    }

    [Fact]
    public async Task The_step_up_is_checked_before_the_login_is_looked_up()
    {
        // A session that has not stepped up learns nothing about which ids are its own.
        _currentUser.AuthTime.Returns((DateTimeOffset?)null);

        var result = await _sut.HandleAsync(new UnlinkExternalLoginCommand(Guid.NewGuid()), Ct);

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
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
        var login = _user.ExternalLogins.ShouldHaveSingleItem();
        _mfaMethods.HasConfirmedAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(true);
        _currentUser.AuthMethods.Returns(amr.Split(' '));
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([_current]);

        var result = await _sut.HandleAsync(new UnlinkExternalLoginCommand(login.Id), Ct);

        if (allowed)
        {
            result.IsSuccess.ShouldBeTrue();
            return;
        }

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
        _user.ExternalLogins.ShouldHaveSingleItem();
        await AssertNothingSavedAsync();
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("no-session")]
    [InlineData("unknown-user")]
    [InlineData("suspended")]
    [InlineData("unknown-session")]
    [InlineData("revoked-session")]
    [InlineData("changed-stamp")]
    public async Task Caller_without_an_active_session_of_their_own_is_unauthorized(string scenario)
    {
        var login = _user.ExternalLogins.ShouldHaveSingleItem();
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

        var result = await _sut.HandleAsync(new UnlinkExternalLoginCommand(login.Id), Ct);

        // The user-not-found error the endpoint answers like a request without a valid token.
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Code.ShouldBe(UserErrors.NotFound(_user.Id).Code);
        _user.ExternalLogins.ShouldHaveSingleItem();
        _user.DomainEvents.ShouldBeEmpty();
        await AssertNothingSavedAsync();
    }

    private static User PasswordUser()
    {
        var user = User.Register("alice@example.com", "Alice", "en", "stored-hash", SignedInAt.AddDays(-1)).Value;
        user.ConfirmEmail(SignedInAt.AddDays(-1));
        user.LinkExternalLogin("google", "google-subject-0001", "alice@example.com", SignedInAt.AddDays(-1));
        user.ClearDomainEvents();
        return user;
    }

    private static User SocialOnlyUser()
    {
        var user = User.RegisterExternal("bob@example.com", "Bob", "en", "google", "google-subject-0002", "bob@example.com", SignedInAt.AddDays(-1)).Value;
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

    /// <summary>Signs <paramref name="user"/> in with a fresh session and makes it the caller.</summary>
    private UserSession UseCaller(User user)
    {
        var session = StartSession(user);
        _user = user;
        _current = session;
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _sessions.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _sessions.GetActiveByUserAsync(user.Id, Now, Arg.Any<CancellationToken>()).Returns([session]);
        _currentUser.UserId.Returns(user.Id);
        _currentUser.SessionId.Returns(session.Id);
        return session;
    }

    /// <summary>A refusal saves nothing and writes no audit row.</summary>
    private async Task AssertNothingSavedAsync()
    {
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().TrySaveChangesAsync(Ct);
    }
}
