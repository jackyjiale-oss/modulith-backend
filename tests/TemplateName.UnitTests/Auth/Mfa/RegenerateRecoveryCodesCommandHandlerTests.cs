using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Mfa.RegenerateRecoveryCodes;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class RegenerateRecoveryCodesCommandHandlerTests
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
    private readonly List<RecoveryCode> _addedCodes = [];
    private readonly List<RecoveryCode> _removedCodes = [];
    private readonly User _user;
    private readonly UserSession _current;
    private readonly RegenerateRecoveryCodesCommandHandler _sut;

    public RegenerateRecoveryCodesCommandHandlerTests()
    {
        _user = User.Register("alice@example.com", "Alice", "en", "stored-hash", SignedInAt.AddDays(-1)).Value;
        _user.ConfirmEmail(SignedInAt.AddDays(-1));
        _user.ClearDomainEvents();
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);

        _current = StartSession(_user);
        _sessions.GetByIdAsync(_current.Id, Arg.Any<CancellationToken>()).Returns(_current);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([_current]);
        _methods.HasConfirmedAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(true);
        _recoveryCodes.ListByUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns([]);
        _recoveryCodes.Add(Arg.Do<RecoveryCode>(_addedCodes.Add));
        _recoveryCodes.Remove(Arg.Do<RecoveryCode>(_removedCodes.Add));

        _currentUser.UserId.Returns(_user.Id);
        _currentUser.SessionId.Returns(_current.Id);
        _currentUser.AuthTime.Returns(SignedInAt);
        _currentUser.AuthMethods.Returns(["rcvr"]);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));

        var time = new FakeTimeProvider(Now);
        _sut = new RegenerateRecoveryCodesCommandHandler(
            _users,
            _sessions,
            _methods,
            new RecoveryCodeIssuer(_recoveryCodes),
            new MfaChangeEffects(_sessions),
            new StepUpPolicy(_currentUser, Options.Create(new StepUpOptions()), time),
            _currentUser,
            _auditWriter,
            _unitOfWork,
            time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Regenerate_without_mfa_is_not_enabled()
    {
        // Answered before the step-up: a password-only account could never satisfy it, so the useful answer is that there is nothing to do.
        _methods.HasConfirmedAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(false);
        _currentUser.AuthMethods.Returns(["pwd"]);

        var result = await _sut.HandleAsync(new RegenerateRecoveryCodesCommand(), Ct);

        result.Error.ShouldBe(MfaErrors.NotEnabled);
        _addedCodes.ShouldBeEmpty();
        _removedCodes.ShouldBeEmpty();
        _user.DomainEvents.ShouldBeEmpty();
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Regenerate_replaces_every_code()
    {
        var oldStamp = _user.SecurityStamp;
        var oldCodes = new[]
        {
            RecoveryCode.Create(_user.Id, RecoveryCodeFormat.Hash(RecoveryCodeFormat.Generate()), Now.AddDays(-9)),
            RecoveryCode.Create(_user.Id, RecoveryCodeFormat.Hash(RecoveryCodeFormat.Generate()), Now.AddDays(-9)),
        };
        _recoveryCodes.ListByUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(oldCodes);
        var other = StartSession(_user);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([other, _current]);

        var result = await _sut.HandleAsync(new RegenerateRecoveryCodesCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        var codes = result.Value.RecoveryCodes;
        codes.Count.ShouldBe(RecoveryCodeFormat.Count);
        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(RecoveryCodeFormat.Count);

        // Every old code is removed; only the hash of each new one is stored.
        _removedCodes.ShouldBe(oldCodes);
        _addedCodes.Count.ShouldBe(RecoveryCodeFormat.Count);
        _addedCodes.ShouldAllBe(code => code.UserId == _user.Id && code.CreatedAt == Now && code.UsedAt == null);
        _addedCodes.Select(code => Convert.ToHexString(code.CodeHash)).ShouldBe(
            codes.Select(code => Convert.ToHexString(RecoveryCodeFormat.Hash(RecoveryCodeFormat.Normalize(code)!))),
            ignoreOrder: true);

        _user.SecurityStamp.ShouldNotBe(oldStamp);
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MfaChangedDomainEvent(_user.Id, MfaChange.RecoveryCodesRegenerated));
        other.RevokedReason.ShouldBe(SessionRevokedReason.MfaChanged);
        _current.RevokedAt.ShouldBeNull();
        _current.SecurityStamp.ShouldBe(_user.SecurityStamp);

        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.RecoveryCodesRegenerated);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_current.Id);
        audit.Details.ShouldBe("""{"revokedSessionCount":1}""");
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);

        result.Value.ToString().ShouldBe(nameof(RecoveryCodesResponse));
    }

    [Theory]
    [InlineData("pwd-only")]
    [InlineData("stale")]
    [InlineData("no-auth-time")]
    public async Task Regenerate_needs_a_fresh_mfa_step_up(string scenario)
    {
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

        var result = await _sut.HandleAsync(new RegenerateRecoveryCodesCommand(), Ct);

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
        _addedCodes.ShouldBeEmpty();
        _removedCodes.ShouldBeEmpty();
        _user.DomainEvents.ShouldBeEmpty();
        _auditEntries.ShouldBeEmpty();
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

        var result = await _sut.HandleAsync(new RegenerateRecoveryCodesCommand(), Ct);

        // The user-not-found error the endpoint answers like a request without a valid token.
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Code.ShouldBe(UserErrors.NotFound(_user.Id).Code);
        _addedCodes.ShouldBeEmpty();
        _removedCodes.ShouldBeEmpty();
        _user.DomainEvents.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
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
