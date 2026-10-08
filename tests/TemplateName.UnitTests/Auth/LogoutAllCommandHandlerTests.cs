using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication.LogoutAll;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;

namespace TemplateName.UnitTests.Auth;

public sealed class LogoutAllCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly LogoutAllCommandHandler _sut;

    public LogoutAllCommandHandlerTests()
    {
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _sut = new LogoutAllCommandHandler(_sessions, _currentUser, _auditWriter, _unitOfWork, new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Logout_all_revokes_every_active_session_of_the_user()
    {
        var current = StartSession();
        var other = StartSession();
        _currentUser.UserId.Returns(UserId);
        _currentUser.SessionId.Returns(current.Id);
        _sessions.GetActiveByUserAsync(UserId, Now, Arg.Any<CancellationToken>()).Returns([current, other]);

        var result = await _sut.HandleAsync(new LogoutAllCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        foreach (var session in new[] { current, other })
        {
            session.RevokedAt.ShouldBe(Now);
            session.RevokedReason.ShouldBe(SessionRevokedReason.LogoutAll);
            session.RefreshTokens.ShouldAllBe(token => token.RevokedAt == Now);
        }

        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.LogoutAll);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(UserId);
        audit.SessionId.ShouldBe(current.Id);
        audit.Details.ShouldBe("""{"sessionCount":2}""");
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Logout_all_without_active_sessions_still_succeeds_and_audits()
    {
        _currentUser.UserId.Returns(UserId);
        _sessions.GetActiveByUserAsync(UserId, Now, Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.HandleAsync(new LogoutAllCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        _auditEntries.ShouldHaveSingleItem().Details.ShouldBe("""{"sessionCount":0}""");
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Logout_all_without_a_user_is_a_noop_success()
    {
        _currentUser.UserId.Returns((Guid?)null);

        var result = await _sut.HandleAsync(new LogoutAllCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        await _sessions.DidNotReceiveWithAnyArgs().GetActiveByUserAsync(default, default, Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    private static UserSession StartSession()
        => UserSession.Start(
            UserId, "pwd", "Laptop", null, null, "stamp", [.. Enumerable.Repeat((byte)1, 32)], TimeSpan.FromDays(14), TimeSpan.FromDays(90), Now.AddHours(-1)).Session;
}
