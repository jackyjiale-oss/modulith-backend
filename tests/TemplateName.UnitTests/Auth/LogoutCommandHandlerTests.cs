using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication.Logout;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;

namespace TemplateName.UnitTests.Auth;

public sealed class LogoutCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly LogoutCommandHandler _sut;

    public LogoutCommandHandlerTests()
    {
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _currentUser.UserId.Returns(UserId);
        _sut = new LogoutCommandHandler(_sessions, _currentUser, _auditWriter, _unitOfWork, new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Logout_without_session_claim_is_a_noop_success()
    {
        _currentUser.SessionId.Returns((Guid?)null);

        var result = await _sut.HandleAsync(new LogoutCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        await _sessions.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Logout_revokes_the_current_session_and_audits()
    {
        var session = GivenCurrentSession(UserId);

        var result = await _sut.HandleAsync(new LogoutCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        session.RevokedAt.ShouldBe(Now);
        session.RevokedReason.ShouldBe(SessionRevokedReason.Logout);
        session.RefreshTokens.ShouldAllBe(token => token.RevokedAt == Now);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.Logout);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(UserId);
        audit.SessionId.ShouldBe(session.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Logout_of_an_already_revoked_session_succeeds_and_changes_nothing()
    {
        var session = GivenCurrentSession(UserId);
        session.Revoke(SessionRevokedReason.PasswordChanged, Now.AddMinutes(-3));

        var result = await _sut.HandleAsync(new LogoutCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        session.RevokedAt.ShouldBe(Now.AddMinutes(-3));
        session.RevokedReason.ShouldBe(SessionRevokedReason.PasswordChanged);
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Logout_leaves_a_session_of_another_user_alone()
    {
        var session = GivenCurrentSession(Guid.NewGuid());

        var result = await _sut.HandleAsync(new LogoutCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        session.RevokedAt.ShouldBeNull();
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Logout_of_an_unknown_session_is_a_noop_success()
    {
        _currentUser.SessionId.Returns(Guid.NewGuid());

        var result = await _sut.HandleAsync(new LogoutCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    private UserSession GivenCurrentSession(Guid ownerId)
    {
        var (session, _) = UserSession.Start(
            ownerId, "pwd", "Laptop", null, null, "stamp", [.. Enumerable.Repeat((byte)1, 32)], TimeSpan.FromDays(14), TimeSpan.FromDays(90), Now.AddHours(-1));
        _currentUser.SessionId.Returns(session.Id);
        _sessions.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        return session;
    }
}
