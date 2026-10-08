using NSubstitute;
using TemplateName.Modules.Auth.Application.Admin.Users.AssignRoles;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class AssignRolesCommandHandlerTests : AdminHandlerTestBase
{
    private readonly AssignRolesCommandHandler _sut;

    public AssignRolesCommandHandlerTests()
    {
        _sut = new AssignRolesCommandHandler(Users, Roles, Rules, GrantRules, PermissionCache, AuditWriter, UnitOfWork, Time);
    }

    [Fact]
    public async Task Last_super_admin_cannot_lose_the_role()
    {
        // A SuperAdmin demoting themselves while no other SuperAdmin is active would leave nobody able to administer SuperAdmins.
        GivenActorIsSuperAdmin();
        var target = GivenUser(SuperAdminRole);
        Users.GetByIdAsync(ActorId, Arg.Any<CancellationToken>()).Returns(target);
        Users.AnyOtherActiveInRoleAsync(SuperAdminRole.Id, target.Id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, target.Id, [UserRole.Id]), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("auth.last_super_admin");
        result.Error.Type.ShouldBe(ErrorType.Conflict);
        target.Roles.Select(role => role.RoleId).ShouldBe([SuperAdminRole.Id]);
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        await PermissionCache.DidNotReceiveWithAnyArgs().InvalidateUsersAsync(default!, Ct);
    }

    [Fact]
    public async Task Super_admin_may_lose_the_role_while_another_one_stays_active()
    {
        GivenActorIsSuperAdmin();
        var target = GivenUser(SuperAdminRole);
        Users.AnyOtherActiveInRoleAsync(SuperAdminRole.Id, target.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, target.Id, [UserRole.Id]), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.Roles.Select(role => role.RoleId).ShouldBe([UserRole.Id]);
    }

    [Fact]
    public async Task Assigning_replaces_the_set_audits_and_clears_the_permission_cache_after_saving()
    {
        var support = GivenRole("Support");
        var auditor = GivenRole("Auditor");
        var target = GivenUser(UserRole, support);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, target.Id, [auditor.Id, support.Id, auditor.Id]), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.Roles.Select(role => role.RoleId).ShouldBe([support.Id, auditor.Id], ignoreOrder: true);
        target.Roles.Single(role => role.RoleId == auditor.Id).AssignedBy.ShouldBe(ActorId);
        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.AdminRolesAssigned);
        audit.UserId.ShouldBe(target.Id);
        ActorOf(audit).ShouldBe(ActorId);
        Received.InOrder(() =>
        {
            UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            PermissionCache.InvalidateUsersAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == target.Id), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task An_empty_set_removes_every_role()
    {
        var target = GivenUser(UserRole);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, target.Id, []), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.Roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_or_deleted_role_returns_role_not_found_and_changes_nothing()
    {
        var target = GivenUser(UserRole);
        var unknown = Guid.NewGuid();

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, target.Id, [UserRole.Id, unknown]), Ct);

        result.Error.Code.ShouldBe("auth.role_not_found");
        result.Error.Parameters!["id"].ShouldBe(unknown);
        target.Roles.Select(role => role.RoleId).ShouldBe([UserRole.Id]);
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Non_super_admin_cannot_grant_super_admin_even_to_themselves()
    {
        GivenActorIsSuperAdmin(false);
        var actor = GivenUser(UserRole);
        Users.GetByIdAsync(ActorId, Arg.Any<CancellationToken>()).Returns(actor);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, ActorId, [UserRole.Id, SuperAdminRole.Id]), Ct);

        result.Error.Code.ShouldBe("auth.cannot_manage_super_admin");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
        actor.Roles.Select(role => role.RoleId).ShouldBe([UserRole.Id]);
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Non_super_admin_cannot_change_the_roles_of_a_super_admin()
    {
        GivenActorIsSuperAdmin(false);
        var target = GivenUser(SuperAdminRole);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, target.Id, [SuperAdminRole.Id, UserRole.Id]), Ct);

        result.Error.Code.ShouldBe("auth.cannot_manage_super_admin");
        target.Roles.Select(role => role.RoleId).ShouldBe([SuperAdminRole.Id]);
    }

    [Fact]
    public async Task Super_admin_can_grant_super_admin()
    {
        GivenActorIsSuperAdmin();
        var target = GivenUser(UserRole);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, target.Id, [SuperAdminRole.Id]), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.Roles.Select(role => role.RoleId).ShouldBe([SuperAdminRole.Id]);
    }

    [Fact]
    public async Task Actor_lacking_a_permission_of_a_new_role_gets_permission_grant_not_allowed_and_nothing_changes()
    {
        // assign_roles alone must not let its holder take (or hand out) a role that grants more than they hold.
        var view = GivenPermission("auth.user.view");
        var manage = GivenPermission("auth.role.manage");
        var powerful = GivenRole("Powerful", view, manage);
        ActorHolds.Add("auth.user.view");
        var target = GivenUser(UserRole);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, target.Id, [UserRole.Id, powerful.Id]), Ct);

        result.Error.Code.ShouldBe("auth.permission_grant_not_allowed");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
        target.Roles.Select(role => role.RoleId).ShouldBe([UserRole.Id]);
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        await PermissionCache.DidNotReceiveWithAnyArgs().InvalidateUsersAsync(default!, Ct);
    }

    [Fact]
    public async Task Actor_lacking_a_permission_cannot_give_the_role_to_themselves()
    {
        var manage = GivenPermission("auth.role.manage");
        var powerful = GivenRole("Powerful", manage);
        var actor = GivenUser(UserRole);
        Users.GetByIdAsync(ActorId, Arg.Any<CancellationToken>()).Returns(actor);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, ActorId, [UserRole.Id, powerful.Id]), Ct);

        result.Error.Code.ShouldBe("auth.permission_grant_not_allowed");
        actor.Roles.Select(role => role.RoleId).ShouldBe([UserRole.Id]);
    }

    [Fact]
    public async Task Actor_holding_every_permission_of_a_new_role_may_assign_it_and_deprecated_ones_are_not_required()
    {
        var view = GivenPermission("auth.user.view");
        var withdrawn = GivenPermission("auth.user.withdrawn", deprecated: true);
        var support = GivenRole("Support", view, withdrawn);
        ActorHolds.Add("auth.user.view");
        var target = GivenUser(UserRole);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, target.Id, [UserRole.Id, support.Id]), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.Roles.Select(role => role.RoleId).ShouldBe([UserRole.Id, support.Id], ignoreOrder: true);
        await PermissionChecker.Received(1).HasPermissionAsync(ActorId, "auth.user.view", Arg.Any<CancellationToken>());
        await PermissionChecker.DidNotReceive().HasPermissionAsync(ActorId, "auth.user.withdrawn", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Roles_the_user_already_holds_and_removed_roles_are_not_checked()
    {
        // Keeping or removing a role escalates nothing, so an actor who lacks its permissions may still edit the rest of the set.
        var manage = GivenPermission("auth.role.manage");
        var powerful = GivenRole("Powerful", manage);
        var other = GivenRole("Other", manage);
        var target = GivenUser(UserRole, powerful, other);

        var result = await _sut.HandleAsync(new AssignRolesCommand(ActorId, target.Id, [powerful.Id]), Ct);

        result.IsSuccess.ShouldBeTrue();
        target.Roles.Select(role => role.RoleId).ShouldBe([powerful.Id]);
        await PermissionChecker.DidNotReceiveWithAnyArgs().HasPermissionAsync(default, default!, Ct);
    }
}
