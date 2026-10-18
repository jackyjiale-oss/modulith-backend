using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Mfa.RemoveMethod;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class RemoveMfaMethodCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedInAt = Now.AddMinutes(-3);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IMfaMethodRepository _methods = Substitute.For<IMfaMethodRepository>();
    private readonly IRecoveryCodeRepository _recoveryCodes = Substitute.For<IRecoveryCodeRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<UserMfaMethod> _removedMethods = [];
    private readonly List<RecoveryCode> _removedCodes = [];
    private readonly User _user;
    private readonly UserSession _current;
    private readonly RemoveMfaMethodCommandHandler _sut;

    public RemoveMfaMethodCommandHandlerTests()
    {
        _user = User.Register("alice@example.com", "Alice", "en", "stored-hash", SignedInAt.AddDays(-1)).Value;
        _user.ConfirmEmail(SignedInAt.AddDays(-1));
        _user.ClearDomainEvents();
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);

        _current = StartSession(_user);
        _sessions.GetByIdAsync(_current.Id, Arg.Any<CancellationToken>()).Returns(_current);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([_current]);
        _recoveryCodes.ListByUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns([]);
        _methods.Remove(Arg.Do<UserMfaMethod>(_removedMethods.Add));
        _recoveryCodes.Remove(Arg.Do<RecoveryCode>(_removedCodes.Add));

        _currentUser.UserId.Returns(_user.Id);
        _currentUser.SessionId.Returns(_current.Id);
        _currentUser.AuthTime.Returns(SignedInAt);
        _currentUser.AuthMethods.Returns(["otp"]);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));

        var time = new FakeTimeProvider(Now);
        _sut = new RemoveMfaMethodCommandHandler(
            _users,
            _sessions,
            _methods,
            _recoveryCodes,
            new MfaChangeEffects(_sessions),
            new StepUpPolicy(_currentUser, Options.Create(new StepUpOptions()), time),
            _currentUser,
            _auditWriter,
            _unitOfWork,
            time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Removing_a_pending_method_changes_nothing_else()
    {
        // A pending setup protects nothing: no MFA step-up is needed (the token's amr is only pwd), and no stamp, session, code or
        // audit row is touched.
        _currentUser.AuthMethods.Returns(["pwd"]);
        var pending = Pending(_user);
        var oldStamp = _user.SecurityStamp;
        var other = StartSession(_user);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([_current, other]);

        var result = await _sut.HandleAsync(new RemoveMfaMethodCommand(pending.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        _removedMethods.ShouldHaveSingleItem().ShouldBeSameAs(pending);
        _user.SecurityStamp.ShouldBe(oldStamp);
        _user.DomainEvents.ShouldBeEmpty();
        other.RevokedAt.ShouldBeNull();
        _current.RevokedAt.ShouldBeNull();
        _current.SecurityStamp.ShouldBe(oldStamp);
        _removedCodes.ShouldBeEmpty();
        await _recoveryCodes.DidNotReceiveWithAnyArgs().ListByUserAsync(default, Ct);
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Removing_the_confirmed_method_disables_mfa_deletes_codes_and_rotates_the_stamp()
    {
        var confirmed = Confirmed(_user);
        var codes = new[]
        {
            RecoveryCode.Create(_user.Id, RecoveryCodeFormat.Hash(RecoveryCodeFormat.Generate()), Now.AddDays(-9)),
            RecoveryCode.Create(_user.Id, RecoveryCodeFormat.Hash(RecoveryCodeFormat.Generate()), Now.AddDays(-9)),
        };
        _recoveryCodes.ListByUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(codes);
        var oldStamp = _user.SecurityStamp;
        var other = StartSession(_user);
        var third = StartSession(_user);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([other, _current, third]);

        var result = await _sut.HandleAsync(new RemoveMfaMethodCommand(confirmed.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        _removedMethods.ShouldHaveSingleItem().ShouldBeSameAs(confirmed);
        _removedCodes.ShouldBe(codes);

        _user.SecurityStamp.ShouldNotBe(oldStamp);
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MfaChangedDomainEvent(_user.Id, MfaChange.Disabled));
        other.RevokedReason.ShouldBe(SessionRevokedReason.MfaChanged);
        third.RevokedReason.ShouldBe(SessionRevokedReason.MfaChanged);
        _current.RevokedAt.ShouldBeNull();
        _current.SecurityStamp.ShouldBe(_user.SecurityStamp);

        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.MfaDisabled);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_current.Id);
        audit.Details.ShouldBe("""{"revokedSessionCount":2}""");

        // One save commits the removals, the stamp, the sessions, the audit row and the outbox row together.
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Theory]
    [InlineData("pwd-only")]
    [InlineData("stale")]
    [InlineData("no-auth-time")]
    public async Task Removing_the_confirmed_method_needs_a_fresh_mfa_step_up(string scenario)
    {
        var confirmed = Confirmed(_user);
        switch (scenario)
        {
            case "pwd-only":
                _currentUser.AuthMethods.Returns(["pwd"]);
                break;
            case "stale":
                _currentUser.AuthTime.Returns(Now - TimeSpan.FromMinutes(10));
                break;
            default:
                _currentUser.AuthTime.Returns((DateTimeOffset?)null);
                break;
        }

        var result = await _sut.HandleAsync(new RemoveMfaMethodCommand(confirmed.Id), Ct);

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
        _removedMethods.ShouldBeEmpty();
        _removedCodes.ShouldBeEmpty();
        _user.DomainEvents.ShouldBeEmpty();
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Another_users_method_is_not_found()
    {
        // The lookup is scoped to the caller, so another user's id is answered exactly like an unknown one, whether or not the caller
        // has stepped up: nothing tells that the method exists.
        var foreignId = Guid.NewGuid();
        _currentUser.AuthMethods.Returns(["pwd"]);

        var result = await _sut.HandleAsync(new RemoveMfaMethodCommand(foreignId), Ct);

        result.Error.Code.ShouldBe(MfaErrors.MethodNotFoundCode);
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Message.ShouldBe(MfaErrors.MethodNotFound(foreignId).Message);
        result.Error.Parameters!["id"].ShouldBe(foreignId);
        await _methods.Received(1).GetForUserAsync(foreignId, _user.Id, Ct);
        _removedMethods.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
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
        var confirmed = Confirmed(_user);
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

        var result = await _sut.HandleAsync(new RemoveMfaMethodCommand(confirmed.Id), Ct);

        // The user-not-found error the endpoint answers like a request without a valid token; the method is never looked up.
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Code.ShouldBe(UserErrors.NotFound(_user.Id).Code);
        await _methods.DidNotReceiveWithAnyArgs().GetForUserAsync(default, default, Ct);
        _removedMethods.ShouldBeEmpty();
        _user.DomainEvents.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    private UserMfaMethod Pending(User owner)
    {
        var method = UserMfaMethod.StartTotp(owner.Id, "Phone", "protected-secret", Now.AddMinutes(-1));
        _methods.GetForUserAsync(method.Id, owner.Id, Arg.Any<CancellationToken>()).Returns(method);
        return method;
    }

    private UserMfaMethod Confirmed(User owner)
    {
        var method = Pending(owner);
        method.Confirm(Totp.TimeStepAt(Now) - 5, Now.AddMinutes(-3)).IsSuccess.ShouldBeTrue();
        return method;
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
}
