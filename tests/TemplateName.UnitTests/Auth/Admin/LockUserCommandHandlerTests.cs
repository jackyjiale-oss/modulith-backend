using NSubstitute;
using TemplateName.Modules.Auth.Application.Admin.Users.Lock;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class LockUserCommandHandlerTests : AdminHandlerTestBase
{
    private readonly LockUserCommandHandler _sut;

    public LockUserCommandHandlerTests()
    {
        _sut = new LockUserCommandHandler(Users, Sessions, Rules, PermissionCache, AuditWriter, UnitOfWork, Time);
    }

    [Fact]
    public async Task Cannot_lock_self()
    {
        GivenUserWithId(ActorId);

        var result = await _sut.HandleAsync(new LockUserCommand(ActorId, ActorId), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.cannot_lock_self");
        result.Error.Type.ShouldBe(ErrorType.Validation);
        await Users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Unknown_user_returns_user_not_found()
    {
        var id = Guid.NewGuid();

        var result = await _sut.HandleAsync(new LockUserCommand(ActorId, id), Ct);

        result.Error.Code.ShouldBe("auth.user_not_found");
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Lock_suspends_rotates_the_stamp_revokes_every_session_audits_and_clears_the_permission_cache()
    {
        var target = GivenUser(UserRole);
        var stamp = target.SecurityStamp;
        var sessions = GivenActiveSessions(target, 2);

        var result = await _sut.HandleAsync(new LockUserCommand(ActorId, target.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.Status.ShouldBe(UserStatus.Suspended);
        target.SecurityStamp.ShouldNotBe(stamp);
        sessions.ShouldAllBe(session => session.RevokedReason == SessionRevokedReason.AdminRevoked && session.RevokedAt == Now);
        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.AdminUserLocked);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(target.Id);
        ActorOf(audit).ShouldBe(ActorId);
        Received.InOrder(() =>
        {
            UnitOfWork.SaveChangesAsync(CancellationToken.None);
            PermissionCache.InvalidateUsersAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == target.Id), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Admin_cannot_lock_a_super_admin()
    {
        GivenActorIsSuperAdmin(false);
        var target = GivenUser(SuperAdminRole);

        var result = await _sut.HandleAsync(new LockUserCommand(ActorId, target.Id), Ct);

        result.Error.Code.ShouldBe("auth.cannot_manage_super_admin");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
        target.Status.ShouldBe(UserStatus.Active);
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Super_admin_can_lock_another_super_admin_while_another_one_stays_active()
    {
        GivenActorIsSuperAdmin();
        var target = GivenUser(SuperAdminRole);
        Users.AnyOtherActiveInRoleAsync(SuperAdminRole.Id, target.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HandleAsync(new LockUserCommand(ActorId, target.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.Status.ShouldBe(UserStatus.Suspended);
    }

    [Fact]
    public async Task Last_active_super_admin_cannot_be_locked()
    {
        GivenActorIsSuperAdmin();
        var target = GivenUser(SuperAdminRole);
        Users.AnyOtherActiveInRoleAsync(SuperAdminRole.Id, target.Id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(new LockUserCommand(ActorId, target.Id), Ct);

        result.Error.Code.ShouldBe("auth.last_super_admin");
        result.Error.Type.ShouldBe(ErrorType.Conflict);
        target.Status.ShouldBe(UserStatus.Active);
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Locking_a_suspended_user_again_changes_nothing_but_is_audited()
    {
        var target = GivenUser(UserRole);
        target.Suspend(Now.AddDays(-1));
        var stamp = target.SecurityStamp;

        var result = await _sut.HandleAsync(new LockUserCommand(ActorId, target.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.SecurityStamp.ShouldBe(stamp);
        AuditEntries.ShouldHaveSingleItem().EventType.ShouldBe(AuthAuditEvents.AdminUserLocked);
    }

    private void GivenUserWithId(Guid id)
    {
        var user = GivenUser(UserRole);
        Users.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(user);
    }
}
