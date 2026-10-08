using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class RoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> SystemRoleNames => new() { SystemRoles.SuperAdmin, SystemRoles.Admin, SystemRoles.User };

    [Fact]
    public void Create_normalizes_name_and_rejects_blank()
    {
        var role = Role.Create("  Support Agent ", "Handles tickets", Now).Value;

        role.Name.ShouldBe("Support Agent");
        role.NormalizedName.ShouldBe("SUPPORT AGENT");
        role.Description.ShouldBe("Handles tickets");
        role.IsSystem.ShouldBeFalse();
        role.Id.ShouldNotBe(Guid.Empty);
        role.Permissions.ShouldBeEmpty();
        Role.NormalizeName(" Support Agent ").ShouldBe("SUPPORT AGENT");

        Should.Throw<ArgumentException>(() => Role.Create(string.Empty, "x", Now));
        Should.Throw<ArgumentException>(() => Role.Create("   ", "x", Now));
    }

    [Theory]
    [MemberData(nameof(SystemRoleNames))]
    public void System_role_cannot_be_renamed_or_deleted(string name)
    {
        var role = Role.CreateSystem(name, "Built in", Now);

        role.IsSystem.ShouldBeTrue();
        role.Name.ShouldBe(name);
        role.UpdateDetails("Other", "Other").Error.ShouldBe(RoleErrors.SystemRoleProtected);
        role.MarkDeleted().Error.ShouldBe(RoleErrors.SystemRoleProtected);
        role.Name.ShouldBe(name);
        role.Description.ShouldBe("Built in");
    }

    [Fact]
    public void Custom_role_can_be_renamed_and_deleted()
    {
        var role = Role.Create("Support", "Old", Now).Value;

        role.UpdateDetails(" Helpdesk ", "New").IsSuccess.ShouldBeTrue();
        role.Name.ShouldBe("Helpdesk");
        role.NormalizedName.ShouldBe("HELPDESK");
        role.Description.ShouldBe("New");
        role.MarkDeleted().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Deleting_a_role_drops_its_grants_but_a_refused_deletion_keeps_them()
    {
        var custom = Role.Create("Support", "Tickets", Now).Value;
        var admin = Role.CreateSystem(SystemRoles.Admin, "Administrators", Now);
        var permissionId = Guid.NewGuid();
        custom.SetPermissions([permissionId]);
        admin.SetPermissions([permissionId]);

        admin.MarkDeleted().Error.ShouldBe(RoleErrors.SystemRoleProtected);
        admin.Permissions.Count.ShouldBe(1);

        custom.MarkDeleted().IsSuccess.ShouldBeTrue();
        custom.Permissions.ShouldBeEmpty();
    }

    [Fact]
    public void Name_and_description_limits_are_the_column_lengths()
    {
        Role.MaxNameLength.ShouldBe(100);
        Role.MaxDescriptionLength.ShouldBe(500);
    }

    [Fact]
    public void SuperAdmin_permissions_cannot_be_set_by_hand()
    {
        var superAdmin = Role.CreateSystem(SystemRoles.SuperAdmin, "Everything", Now);
        var admin = Role.CreateSystem(SystemRoles.Admin, "Administrators", Now);
        var custom = Role.Create("Support", "Tickets", Now).Value;
        var permissionId = Guid.NewGuid();

        superAdmin.SetPermissions([permissionId]).Error.ShouldBe(RoleErrors.SystemRoleProtected);
        superAdmin.Permissions.ShouldBeEmpty();

        admin.SetPermissions([permissionId]).IsSuccess.ShouldBeTrue();
        custom.SetPermissions([permissionId]).IsSuccess.ShouldBeTrue();
        admin.Permissions.Single().PermissionId.ShouldBe(permissionId);
        custom.Permissions.Single().RoleId.ShouldBe(custom.Id);
    }

    [Theory]
    [InlineData("SuperAdmin", true)]
    [InlineData("superadmin", true)]
    [InlineData(" SUPERADMIN ", true)]
    [InlineData("Admin", false)]
    public void SuperAdmin_is_recognised_by_its_normalized_name(string name, bool isSuperAdmin)
    {
        var system = Role.CreateSystem(name, "System", Now);
        var custom = Role.Create(name, "Custom", Now).Value;

        system.IsSuperAdmin.ShouldBe(isSuperAdmin);
        system.SetPermissions([Guid.NewGuid()]).IsFailure.ShouldBe(isSuperAdmin);

        // Only the system role is protected: a custom role cannot become SuperAdmin by its name.
        custom.IsSuperAdmin.ShouldBeFalse();
    }

    [Fact]
    public void SyncPermissions_replaces_the_set_for_any_role()
    {
        var superAdmin = Role.CreateSystem(SystemRoles.SuperAdmin, "Everything", Now);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        superAdmin.SyncPermissions([first, second]);
        superAdmin.Permissions.Select(p => p.PermissionId).ShouldBe([first, second], ignoreOrder: true);

        superAdmin.SyncPermissions([second, third]);
        superAdmin.Permissions.Select(p => p.PermissionId).ShouldBe([second, third], ignoreOrder: true);
        superAdmin.Permissions.ShouldAllBe(p => p.RoleId == superAdmin.Id);

        superAdmin.SyncPermissions([]);
        superAdmin.Permissions.ShouldBeEmpty();
    }

    [Fact]
    public void SetPermissions_is_idempotent_and_deduplicates()
    {
        var role = Role.Create("Support", "Tickets", Now).Value;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        role.SetPermissions([first, second, first]).IsSuccess.ShouldBeTrue();
        role.SetPermissions([second, first]).IsSuccess.ShouldBeTrue();

        role.Permissions.Count.ShouldBe(2);
        role.Permissions.Select(p => p.PermissionId).ShouldBe([first, second], ignoreOrder: true);

        role.SetPermissions([second]).IsSuccess.ShouldBeTrue();
        role.Permissions.Single().PermissionId.ShouldBe(second);
    }

    [Fact]
    public void Permission_is_created_updated_and_deprecated()
    {
        var permission = Permission.Create("auth.user.view", "auth", "View users", "List and read users.", Now);

        permission.Id.ShouldNotBe(Guid.Empty);
        permission.Code.ShouldBe("auth.user.view");
        permission.Module.ShouldBe("auth");
        permission.IsDeprecated.ShouldBeFalse();

        permission.Describe("Read users", "Reads users.");
        permission.Name.ShouldBe("Read users");
        permission.Description.ShouldBe("Reads users.");

        permission.SetDeprecated(true);
        permission.IsDeprecated.ShouldBeTrue();
        permission.SetDeprecated(false);
        permission.IsDeprecated.ShouldBeFalse();
    }

    [Fact]
    public void Role_errors_carry_codes_and_types()
    {
        var id = Guid.NewGuid();

        RoleErrors.NotFound(id).Code.ShouldBe("auth.role_not_found");
        RoleErrors.NotFound(id).Type.ShouldBe(ErrorType.NotFound);
        RoleErrors.NotFound(id).Parameters!["id"].ShouldBe(id);
        RoleErrors.NameTaken("Support").Code.ShouldBe("auth.role_name_taken");
        RoleErrors.NameTaken("Support").Type.ShouldBe(ErrorType.Conflict);
        RoleErrors.NameTaken("Support").Parameters!["name"].ShouldBe("Support");
        RoleErrors.SystemRoleProtected.Code.ShouldBe("auth.system_role_protected");
        RoleErrors.SystemRoleProtected.Type.ShouldBe(ErrorType.Conflict);
        RoleErrors.PermissionNotFound.Code.ShouldBe("auth.permission_not_found");
        RoleErrors.PermissionNotFound.Type.ShouldBe(ErrorType.Validation);
    }
}
