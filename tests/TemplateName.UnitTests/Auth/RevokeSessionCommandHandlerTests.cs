using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Sessions.Revoke;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class RevokeSessionCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly RevokeSessionCommandHandler _sut;

    public RevokeSessionCommandHandlerTests()
    {
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _currentUser.UserId.Returns(UserId);
        _sut = new RevokeSessionCommandHandler(_sessions, _currentUser, _auditWriter, _unitOfWork, new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Revoke_unknown_session_returns_not_found()
    {
        var id = Guid.NewGuid();

        var result = await _sut.HandleAsync(new RevokeSessionCommand(id), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.session_not_found");
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Revoking_a_session_of_another_user_looks_exactly_like_an_unknown_one()
    {
        var other = GivenSession(Guid.NewGuid());

        var result = await _sut.HandleAsync(new RevokeSessionCommand(other.Id), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.session_not_found");
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Message.ShouldBe(SessionErrors.NotFound(other.Id).Message);
        other.RevokedAt.ShouldBeNull();
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Anonymous_caller_gets_not_found()
    {
        _currentUser.UserId.Returns((Guid?)null);
        var session = GivenSession(UserId);

        var result = await _sut.HandleAsync(new RevokeSessionCommand(session.Id), Ct);

        result.Error.Code.ShouldBe("auth.session_not_found");
        session.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Revoking_an_own_session_ends_it_with_its_tokens_and_audits()
    {
        var session = GivenSession(UserId);

        var result = await _sut.HandleAsync(new RevokeSessionCommand(session.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        session.RevokedAt.ShouldBe(Now);
        session.RevokedReason.ShouldBe(SessionRevokedReason.Logout);
        session.RefreshTokens.ShouldAllBe(token => token.RevokedAt == Now);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.SessionRevoked);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(UserId);
        audit.SessionId.ShouldBe(session.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Revoking_the_current_session_is_allowed()
    {
        var session = GivenSession(UserId);
        _currentUser.SessionId.Returns(session.Id);

        var result = await _sut.HandleAsync(new RevokeSessionCommand(session.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        session.RevokedReason.ShouldBe(SessionRevokedReason.Logout);
    }

    [Fact]
    public async Task Revoking_an_already_revoked_own_session_succeeds_and_changes_nothing()
    {
        var session = GivenSession(UserId);
        session.Revoke(SessionRevokedReason.PasswordChanged, Now.AddMinutes(-3));

        var result = await _sut.HandleAsync(new RevokeSessionCommand(session.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        session.RevokedAt.ShouldBe(Now.AddMinutes(-3));
        session.RevokedReason.ShouldBe(SessionRevokedReason.PasswordChanged);
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    private UserSession GivenSession(Guid ownerId)
    {
        var (session, _) = UserSession.Start(
            ownerId, "pwd", "Laptop", null, null, "stamp", [.. Enumerable.Repeat((byte)1, 32)], TimeSpan.FromDays(14), TimeSpan.FromDays(90), Now.AddHours(-1));
        _sessions.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        return session;
    }
}
