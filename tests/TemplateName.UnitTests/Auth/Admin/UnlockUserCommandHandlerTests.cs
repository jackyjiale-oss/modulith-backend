using NSubstitute;
using TemplateName.Modules.Auth.Application.Admin.Users.Unlock;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class UnlockUserCommandHandlerTests : AdminHandlerTestBase
{
    private readonly UnlockUserCommandHandler _sut;

    public UnlockUserCommandHandlerTests()
    {
        _sut = new UnlockUserCommandHandler(Users, Rules, PermissionCache, AuditWriter, UnitOfWork, Time);
    }

    [Fact]
    public async Task Unlock_reinstates_the_account_ends_the_login_lockout_and_audits()
    {
        var target = GivenUser(UserRole);
        target.Suspend(Now.AddHours(-2));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            target.RecordFailedSignIn(Now.AddMinutes(-1), maxFailedAttempts: 5, TimeSpan.FromMinutes(15));
        }

        target.IsLockedOut(Now).ShouldBeTrue();

        var result = await _sut.HandleAsync(new UnlockUserCommand(ActorId, target.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.Status.ShouldBe(UserStatus.Active);
        target.IsLockedOut(Now).ShouldBeFalse();
        target.AccessFailedCount.ShouldBe(0);
        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.AdminUserUnlocked);
        audit.UserId.ShouldBe(target.Id);
        ActorOf(audit).ShouldBe(ActorId);
        await UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await PermissionCache.Received(1).InvalidateUsersAsync(
            Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == target.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Admin_cannot_unlock_a_super_admin()
    {
        GivenActorIsSuperAdmin(false);
        var target = GivenUser(SuperAdminRole);
        target.Suspend(Now.AddHours(-2));

        var result = await _sut.HandleAsync(new UnlockUserCommand(ActorId, target.Id), Ct);

        result.Error.Code.ShouldBe("auth.cannot_manage_super_admin");
        target.Status.ShouldBe(UserStatus.Suspended);
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Unknown_user_returns_user_not_found()
    {
        var result = await _sut.HandleAsync(new UnlockUserCommand(ActorId, Guid.NewGuid()), Ct);

        result.Error.Code.ShouldBe("auth.user_not_found");
    }
}
