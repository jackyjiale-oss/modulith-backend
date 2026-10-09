using NSubstitute;
using TemplateName.Modules.Auth.Application.Admin.Users.RevokeSessions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class RevokeUserSessionsCommandHandlerTests : AdminHandlerTestBase
{
    private readonly RevokeUserSessionsCommandHandler _sut;

    public RevokeUserSessionsCommandHandlerTests()
    {
        _sut = new RevokeUserSessionsCommandHandler(Users, Sessions, Rules, AuditWriter, UnitOfWork, Time);
    }

    [Fact]
    public async Task Revokes_every_active_session_and_audits_without_touching_the_account()
    {
        var target = GivenUser(UserRole);
        var stamp = target.SecurityStamp;
        var sessions = GivenActiveSessions(target, 3);

        var result = await _sut.HandleAsync(new RevokeUserSessionsCommand(ActorId, target.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        sessions.ShouldAllBe(session => session.RevokedReason == SessionRevokedReason.AdminRevoked && session.RevokedAt == Now);
        target.Status.ShouldBe(UserStatus.Active);
        target.SecurityStamp.ShouldBe(stamp);
        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.AdminSessionsRevoked);
        audit.UserId.ShouldBe(target.Id);
        ActorOf(audit).ShouldBe(ActorId);
        await UnitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Admin_cannot_revoke_the_sessions_of_a_super_admin()
    {
        GivenActorIsSuperAdmin(false);
        var target = GivenUser(SuperAdminRole);
        var sessions = GivenActiveSessions(target, 1);

        var result = await _sut.HandleAsync(new RevokeUserSessionsCommand(ActorId, target.Id), Ct);

        result.Error.Code.ShouldBe("auth.cannot_manage_super_admin");
        sessions.ShouldAllBe(session => session.RevokedAt == null);
    }

    [Fact]
    public async Task Unknown_user_returns_user_not_found()
    {
        var result = await _sut.HandleAsync(new RevokeUserSessionsCommand(ActorId, Guid.NewGuid()), Ct);

        result.Error.Code.ShouldBe("auth.user_not_found");
    }
}
