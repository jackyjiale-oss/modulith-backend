using NSubstitute;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Admin.Roles.SetPermissions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class SetRolePermissionsCommandHandlerTests : AdminRoleHandlerTestBase
{
    private readonly SetRolePermissionsCommandHandler _sut;
    private readonly Permission _view;
    private readonly Permission _create;
    private readonly Permission _lock;

    public SetRolePermissionsCommandHandlerTests()
    {
        _sut = new SetRolePermissionsCommandHandler(Roles, Permissions, PermissionChecker, CacheInvalidator, AuditWriter, UnitOfWork, Time);
        _view = GivenPermission(AuthPermissions.UserView);
        _create = GivenPermission(AuthPermissions.UserCreate);
        _lock = GivenPermission(AuthPermissions.UserLock);
        ActorHolds.UnionWith([AuthPermissions.UserView, AuthPermissions.UserCreate, AuthPermissions.UserLock]);
    }

    [Fact]
    public async Task Setting_the_permissions_replaces_the_set_audits_the_codes_and_clears_the_cache_of_its_users_after_saving()
    {
        var role = GivenStoredRole("Support", _view, _create);
        var holder = Guid.NewGuid();
        UsersInRole.Add(holder);

        var result = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, role.Id, [_create.Id, _lock.Id, _lock.Id]), Ct);

        result.IsSuccess.ShouldBeTrue();
        role.Permissions.Select(grant => grant.PermissionId).ShouldBe([_create.Id, _lock.Id], ignoreOrder: true);
        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.RolePermissionsChanged);
        audit.UserId.ShouldBeNull();
        var details = DetailsOf(audit);
        details.GetProperty("actorId").GetGuid().ShouldBe(ActorId);
        details.GetProperty("roleId").GetGuid().ShouldBe(role.Id);
        details.GetProperty("name").GetString().ShouldBe("Support");
        details.GetProperty("added").EnumerateArray().Select(code => code.GetString()).ShouldBe([AuthPermissions.UserLock]);
        details.GetProperty("removed").EnumerateArray().Select(code => code.GetString()).ShouldBe([AuthPermissions.UserView]);
        Received.InOrder(() =>
        {
            UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            PermissionCache.InvalidateUsersAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == holder), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task An_empty_set_removes_every_grant()
    {
        var role = GivenStoredRole("Support", _view);

        var result = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, role.Id, []), Ct);

        result.IsSuccess.ShouldBeTrue();
        role.Permissions.ShouldBeEmpty();
        AuditEntries.ShouldHaveSingleItem().EventType.ShouldBe(AuthAuditEvents.RolePermissionsChanged);
    }

    [Fact]
    public async Task The_same_set_changes_nothing_and_saves_nothing()
    {
        var role = GivenStoredRole("Support", _view, _create);
        UsersInRole.Add(Guid.NewGuid());

        var result = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, role.Id, [_create.Id, _view.Id]), Ct);

        result.IsSuccess.ShouldBeTrue();
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        await PermissionCache.DidNotReceiveWithAnyArgs().InvalidateUsersAsync(default!, Ct);
    }

    [Fact]
    public async Task SuperAdmin_permissions_cannot_be_edited()
    {
        var superAdmin = GivenStoredSystemRole(SystemRoles.SuperAdmin);

        var result = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, superAdmin.Id, [_view.Id]), Ct);

        result.Error.Code.ShouldBe("auth.system_role_protected");
        superAdmin.Permissions.ShouldBeEmpty();
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Admin_and_User_permissions_can_be_edited()
    {
        var admin = GivenStoredSystemRole(SystemRoles.Admin);
        var user = GivenStoredSystemRole(SystemRoles.User);

        (await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, admin.Id, [_view.Id]), Ct)).IsSuccess.ShouldBeTrue();
        (await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, user.Id, [_create.Id]), Ct)).IsSuccess.ShouldBeTrue();

        admin.Permissions.Select(grant => grant.PermissionId).ShouldBe([_view.Id]);
        user.Permissions.Select(grant => grant.PermissionId).ShouldBe([_create.Id]);
    }

    [Fact]
    public async Task An_unknown_permission_id_returns_permission_not_found_and_changes_nothing()
    {
        var role = GivenStoredRole("Support", _view);

        var result = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, role.Id, [_create.Id, Guid.NewGuid()]), Ct);

        result.Error.Code.ShouldBe("auth.permission_not_found");
        result.Error.Type.ShouldBe(ErrorType.Validation);
        role.Permissions.Select(grant => grant.PermissionId).ShouldBe([_view.Id]);
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_deprecated_permission_cannot_be_newly_granted_but_an_existing_grant_may_stay()
    {
        var retired = GivenPermission("auth.old.thing", deprecated: true);
        ActorHolds.Add("auth.old.thing");
        var role = GivenStoredRole("Support", retired);

        // Keeping what it already holds is not a new grant.
        var kept = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, role.Id, [retired.Id, _view.Id]), Ct);
        kept.IsSuccess.ShouldBeTrue();

        // Another role cannot be given it.
        var other = GivenStoredRole("Other");
        var refused = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, other.Id, [retired.Id]), Ct);

        refused.Error.Code.ShouldBe("auth.permission_not_found");
        other.Permissions.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_actor_cannot_grant_a_permission_they_do_not_hold()
    {
        var role = GivenStoredRole("Support", _view);
        ActorHolds.Remove(AuthPermissions.UserLock);

        var result = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, role.Id, [_view.Id, _lock.Id]), Ct);

        result.Error.Code.ShouldBe("auth.permission_grant_not_allowed");
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
        role.Permissions.Select(grant => grant.PermissionId).ShouldBe([_view.Id]);
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task The_actor_may_remove_a_permission_they_do_not_hold_and_keep_one_the_role_already_has()
    {
        // Only a NEW grant is limited by what the actor holds: taking a permission away, or leaving it, escalates nothing.
        var role = GivenStoredRole("Support", _view, _lock);
        ActorHolds.Remove(AuthPermissions.UserLock);

        var removed = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, role.Id, [_view.Id]), Ct);
        removed.IsSuccess.ShouldBeTrue();

        var other = GivenStoredRole("Auditors", _lock);
        var kept = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, other.Id, [_lock.Id, _view.Id]), Ct);
        kept.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_or_deleted_role_returns_role_not_found()
    {
        var result = await _sut.HandleAsync(new SetRolePermissionsCommand(ActorId, Guid.NewGuid(), [_view.Id]), Ct);

        result.Error.Code.ShouldBe("auth.role_not_found");
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }
}
