using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Identity;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

public sealed class AuthSeederTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string AdminEmailKey = "Auth:Seed:AdminEmail";
    private const string AdminPasswordKey = "Auth:Seed:AdminPassword";
    private const string AdminPassword = "correct horse battery staple";

    private static readonly PermissionDefinition Widget = new("test.widget.view", "test", "View widgets", "Read widgets.");

    private static readonly string[] AdminDefaults =
    [
        AuthPermissions.UserView,
        AuthPermissions.UserCreate,
        AuthPermissions.UserLock,
        AuthPermissions.UserResetPassword,
        AuthPermissions.UserRevokeSessions,
        AuthPermissions.RoleView,
        AuthPermissions.PermissionView,
        AuthPermissions.AuditView,
    ];

    private readonly List<AsyncServiceScope> _scopes = [];

    private static IEnumerable<string> AuthCodes => new AuthPermissionSource().Permissions.Select(permission => permission.Code);

    [Fact]
    public async Task Seeding_twice_is_idempotent()
    {
        var first = await SnapshotAsync();

        await Factory.Services.SeedAuthModuleAsync(Ct);

        var second = await SnapshotAsync();
        second.ShouldBe(first);
        (await GrantedCodesAsync(SystemRoles.SuperAdmin)).ShouldBe(AuthCodes, ignoreOrder: true);
        (await GrantedCodesAsync(SystemRoles.Admin)).ShouldBe(AdminDefaults, ignoreOrder: true);
        (await GrantedCodesAsync(SystemRoles.User)).ShouldBeEmpty();
        var roles = await Context().Set<Role>().ToListAsync(Ct);
        roles.Select(role => role.Name).ShouldBe([SystemRoles.SuperAdmin, SystemRoles.Admin, SystemRoles.User], ignoreOrder: true);
        roles.ShouldAllBe(role => role.IsSystem);
        var permissions = await Context().Set<Permission>().ToListAsync(Ct);
        permissions.Select(permission => permission.Code).ShouldBe(AuthCodes, ignoreOrder: true);
        permissions.ShouldAllBe(permission => !permission.IsDeprecated && permission.Module == "auth");
    }

    [Fact]
    public async Task SuperAdmin_receives_every_permission_including_new_ones_on_the_next_run()
    {
        Factory.PermissionSource.Declare(Widget);

        await Factory.Services.SeedAuthModuleAsync(Ct);

        (await GrantedCodesAsync(SystemRoles.SuperAdmin)).ShouldBe([.. AuthCodes, Widget.Code], ignoreOrder: true);
        (await GrantedCodesAsync(SystemRoles.Admin)).ShouldBe(AdminDefaults, ignoreOrder: true);
        (await GrantedCodesAsync(SystemRoles.User)).ShouldBeEmpty();
        var widget = await PermissionAsync(Widget.Code);
        widget.Module.ShouldBe("test");
        widget.Name.ShouldBe("View widgets");
        widget.Description.ShouldBe("Read widgets.");

        // A changed name and description are updated in place.
        Factory.PermissionSource.Declare(Widget with { Name = "Browse widgets", Description = "List and read widgets." });
        await Factory.Services.SeedAuthModuleAsync(Ct);

        var renamed = await PermissionAsync(Widget.Code);
        renamed.Id.ShouldBe(widget.Id);
        renamed.Name.ShouldBe("Browse widgets");
        renamed.Description.ShouldBe("List and read widgets.");
    }

    [Fact]
    public async Task Admin_defaults_are_not_overwritten_after_manual_changes()
    {
        var scope = NewScope();
        var admin = (await scope.GetRequiredService<IRoleRepository>().GetByNormalizedNameAsync("ADMIN", Ct)).ShouldNotBeNull();
        var userView = await PermissionAsync(AuthPermissions.UserView);
        admin.SetPermissions([userView.Id]).IsSuccess.ShouldBeTrue();
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        await Factory.Services.SeedAuthModuleAsync(Ct);

        (await GrantedCodesAsync(SystemRoles.Admin)).ShouldBe([AuthPermissions.UserView]);
    }

    [Fact]
    public async Task Removed_permission_is_marked_deprecated_not_deleted()
    {
        Factory.PermissionSource.Declare(Widget);
        await Factory.Services.SeedAuthModuleAsync(Ct);
        var widget = await PermissionAsync(Widget.Code);
        var adminId = await GrantToAdminAsync(widget.Id);
        var user = await CreateUserAsync(adminId);

        Factory.PermissionSource.Reset();
        await Factory.Services.SeedAuthModuleAsync(Ct);

        var deprecated = await PermissionAsync(Widget.Code);
        deprecated.Id.ShouldBe(widget.Id);
        deprecated.IsDeprecated.ShouldBeTrue();
        (await GrantedCodesAsync(SystemRoles.SuperAdmin)).ShouldNotContain(Widget.Code);
        (await GrantedCodesAsync(SystemRoles.Admin)).ShouldContain(Widget.Code);

        // The grant stays readable, but a deprecated permission is never granted.
        var checker = NewScope().GetRequiredService<IPermissionChecker>();
        (await checker.HasPermissionAsync(user.Id, Widget.Code, Ct)).ShouldBeFalse();
        (await checker.HasPermissionAsync(user.Id, AuthPermissions.UserView, Ct)).ShouldBeTrue();

        // Declared again, it comes back.
        Factory.PermissionSource.Declare(Widget);
        await Factory.Services.SeedAuthModuleAsync(Ct);

        (await PermissionAsync(Widget.Code)).IsDeprecated.ShouldBeFalse();
        (await GrantedCodesAsync(SystemRoles.SuperAdmin)).ShouldContain(Widget.Code);
    }

    [Fact]
    public async Task Invalid_permission_definition_fails_the_seed_naming_the_code_and_changes_nothing()
    {
        var before = await SnapshotAsync();
        Factory.PermissionSource.Declare(Widget, Widget with { Code = "Test.Widget.Edit" });

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => Factory.Services.SeedAuthModuleAsync(Ct));

        exception.Message.ShouldContain("'Test.Widget.Edit'");
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Admin_account_is_seeded_only_when_both_settings_exist()
    {
        await SeedWithSettingsAsync(("admin@localhost.test", null));
        await SeedWithSettingsAsync((null, AdminPassword));
        await SeedWithSettingsAsync(("admin@localhost.test", "   "));
        (await Context().Set<User>().IgnoreQueryFilters().CountAsync(Ct)).ShouldBe(0);

        await SeedWithSettingsAsync(("admin@localhost.test", AdminPassword));

        (await Context().Set<User>().IgnoreQueryFilters().CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Seeded_admin_can_be_found_by_normalized_email_and_is_confirmed()
    {
        await SeedWithSettingsAsync(("  Admin@Localhost.Test ", AdminPassword));

        var scope = NewScope();
        var admin = (await scope.GetRequiredService<IUserRepository>().GetByNormalizedEmailAsync("ADMIN@LOCALHOST.TEST", Ct)).ShouldNotBeNull();
        admin.Email.ShouldBe("Admin@Localhost.Test");
        admin.EmailConfirmed.ShouldBeTrue();
        admin.Status.ShouldBe(UserStatus.Active);
        admin.EnsureCanSignIn().IsSuccess.ShouldBeTrue();
        var superAdmin = (await scope.GetRequiredService<IRoleRepository>().GetByNormalizedNameAsync("SUPERADMIN", Ct)).ShouldNotBeNull();
        admin.Roles.ShouldHaveSingleItem().RoleId.ShouldBe(superAdmin.Id);
        var hash = admin.PasswordHash.ShouldNotBeNull();
        hash.ShouldNotContain(AdminPassword);
        scope.GetRequiredService<IPasswordHasher>().Verify(hash, AdminPassword).ShouldNotBe(PasswordVerification.Failed);
        (await scope.GetRequiredService<IPermissionChecker>().HasPermissionAsync(admin.Id, AuthPermissions.RoleManage, Ct)).ShouldBeTrue();

        // Another spelling of the same address finds the existing account: nothing is added or changed.
        await SeedWithSettingsAsync(("admin@LOCALHOST.test", "another password of enough length"));

        var users = await Context().Set<User>().IgnoreQueryFilters().ToListAsync(Ct);
        users.ShouldHaveSingleItem().PasswordHash.ShouldBe(hash);
    }

    [Fact]
    public async Task Soft_deleted_seeded_admin_is_never_re_created()
    {
        await SeedWithSettingsAsync(("admin@localhost.test", AdminPassword));
        var deleteContext = Context();
        var admin = await deleteContext.Set<User>().SingleAsync(user => user.NormalizedEmail == "ADMIN@LOCALHOST.TEST", Ct);
        deleteContext.Remove(admin);
        await deleteContext.SaveChangesAsync(Ct);
        var before = await SnapshotAsync();

        await SeedWithSettingsAsync(("Admin@Localhost.Test", AdminPassword));

        var users = await Context().Set<User>().IgnoreQueryFilters()
            .Where(user => user.NormalizedEmail == "ADMIN@LOCALHOST.TEST")
            .ToListAsync(Ct);
        users.ShouldHaveSingleItem().Id.ShouldBe(admin.Id);
        users[0].IsDeleted.ShouldBeTrue();
        (await Context().Set<User>().CountAsync(Ct)).ShouldBe(0);
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Too_short_admin_password_fails_the_seed_without_echoing_it()
    {
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => SeedWithSettingsAsync(("admin@localhost.test", "too-short")));

        exception.Message.ShouldContain(AdminPasswordKey);
        exception.Message.ShouldNotContain("too-short");
        (await Context().Set<User>().IgnoreQueryFilters().CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Host_seeds_on_startup_when_run_on_startup_is_set()
    {
        await using var host = Factory.WithWebHostBuilder(builder => builder
            .UseSetting("Auth:Seed:RunOnStartup", "true")
            .UseSetting(AdminEmailKey, "startup@localhost.test")
            .UseSetting(AdminPasswordKey, AdminPassword));

        _ = host.Services;

        var admin = await Context().Set<User>().SingleOrDefaultAsync(user => user.NormalizedEmail == "STARTUP@LOCALHOST.TEST", Ct);
        admin.ShouldNotBeNull().EmailConfirmed.ShouldBeTrue();
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private async Task SeedWithSettingsAsync((string? Email, string? Password) settings)
    {
        await using var host = Factory.WithWebHostBuilder(builder =>
        {
            if (settings.Email is not null)
            {
                builder.UseSetting(AdminEmailKey, settings.Email);
            }

            if (settings.Password is not null)
            {
                builder.UseSetting(AdminPasswordKey, settings.Password);
            }
        });

        await host.Services.SeedAuthModuleAsync(Ct);
    }

    private async Task<List<string>> SnapshotAsync()
    {
        var context = Context();
        var permissions = await context.Set<Permission>().AsNoTracking().OrderBy(permission => permission.Code)
            .Select(permission => $"permission {permission.Id} {permission.Code} {permission.Module} {permission.Name} {permission.Description} {permission.IsDeprecated}")
            .ToListAsync(Ct);
        var roles = await context.Set<Role>().AsNoTracking().Include(role => role.Permissions).OrderBy(role => role.Name).ToListAsync(Ct);
        var users = await context.Set<User>().IgnoreQueryFilters().CountAsync(Ct);

        return
        [
            .. permissions,
            .. roles.Select(role =>
                $"role {role.Id} {role.Name} {role.IsSystem} {string.Join(',', role.Permissions.Select(grant => grant.PermissionId).Order())}"),
            $"users {users}",
        ];
    }

    private async Task<List<string>> GrantedCodesAsync(string roleName)
    {
        var context = Context();
        var grantee = await context.Set<Role>().AsNoTracking().Include(role => role.Permissions).SingleAsync(role => role.Name == roleName, Ct);
        var permissionIds = grantee.Permissions.Select(grant => grant.PermissionId).ToList();
        return await context.Set<Permission>()
            .Where(permission => permissionIds.Contains(permission.Id))
            .Select(permission => permission.Code)
            .ToListAsync(Ct);
    }

    private Task<Permission> PermissionAsync(string code)
        => Context().Set<Permission>().AsNoTracking().SingleAsync(permission => permission.Code == code, Ct);

    private async Task<Guid> GrantToAdminAsync(Guid permissionId)
    {
        var scope = NewScope();
        var admin = (await scope.GetRequiredService<IRoleRepository>().GetByNormalizedNameAsync("ADMIN", Ct)).ShouldNotBeNull();
        admin.SetPermissions([.. admin.Permissions.Select(grant => grant.PermissionId), permissionId]).IsSuccess.ShouldBeTrue();
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        return admin.Id;
    }

    private async Task<User> CreateUserAsync(Guid roleId)
    {
        var now = Factory.Time.GetUtcNow();
        var user = User.Register($"{Guid.NewGuid():N}@example.com", "Test user", "en", "hash", now).Value;
        user.AssignRole(roleId, assignedBy: null, now);
        var context = Context();
        context.Add(user);
        await context.SaveChangesAsync(Ct);
        return user;
    }

    private AuthDbContext Context() => NewScope().GetRequiredService<AuthDbContext>();

    private IServiceProvider NewScope()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider;
    }
}
