using NSubstitute;
using TemplateName.Modules.Auth.Application.Admin.Roles.Delete;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class DeleteRoleCommandHandlerTests : AdminRoleHandlerTestBase
{
    private readonly DeleteRoleCommandHandler _sut;

    public DeleteRoleCommandHandlerTests()
    {
        _sut = new DeleteRoleCommandHandler(Roles, CacheInvalidator, AuditWriter, UnitOfWork, Time);
    }

    [Fact]
    public async Task Delete_drops_the_grants_removes_the_role_audits_and_clears_the_cache_of_its_users_after_saving()
    {
        var view = GivenPermission("auth.user.view");
        var role = GivenStoredRole("Support", view);
        var holder = Guid.NewGuid();
        var otherHolder = Guid.NewGuid();
        UsersInRole.AddRange([holder, otherHolder]);

        var result = await _sut.HandleAsync(new DeleteRoleCommand(ActorId, role.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        role.Permissions.ShouldBeEmpty();
        Roles.Received(1).Remove(role);
        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.RoleDeleted);
        audit.UserId.ShouldBeNull();
        var details = DetailsOf(audit);
        details.GetProperty("actorId").GetGuid().ShouldBe(ActorId);
        details.GetProperty("roleId").GetGuid().ShouldBe(role.Id);
        details.GetProperty("name").GetString().ShouldBe("Support");
        Received.InOrder(() =>
        {
            UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            PermissionCache.InvalidateUsersAsync(
                Arg.Is<IEnumerable<Guid>>(ids => ids.OrderBy(id => id).SequenceEqual(new[] { holder, otherHolder }.OrderBy(id => id))),
                Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData(SystemRoles.SuperAdmin)]
    [InlineData(SystemRoles.Admin)]
    [InlineData(SystemRoles.User)]
    public async Task A_system_role_cannot_be_deleted(string name)
    {
        var role = GivenStoredSystemRole(name);

        var result = await _sut.HandleAsync(new DeleteRoleCommand(ActorId, role.Id), Ct);

        result.Error.Code.ShouldBe("auth.system_role_protected");
        Roles.DidNotReceiveWithAnyArgs().Remove(default!);
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        await PermissionCache.DidNotReceiveWithAnyArgs().InvalidateUsersAsync(default!, Ct);
    }

    [Fact]
    public async Task Unknown_or_already_deleted_role_returns_role_not_found()
    {
        var result = await _sut.HandleAsync(new DeleteRoleCommand(ActorId, Guid.NewGuid()), Ct);

        result.Error.Code.ShouldBe("auth.role_not_found");
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }
}
