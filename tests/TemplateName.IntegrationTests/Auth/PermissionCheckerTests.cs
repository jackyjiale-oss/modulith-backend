using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Identity;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Authorization;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

public sealed class PermissionCheckerTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly List<AsyncServiceScope> _scopes = [];

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task User_gets_the_union_of_permissions_of_all_their_roles()
    {
        var readers = await CreateRoleAsync("Readers", AuthPermissions.UserView);
        var auditors = await CreateRoleAsync("Auditors", AuthPermissions.AuditView, AuthPermissions.RoleView);
        var user = await CreateUserAsync(readers, auditors);

        (await HasPermissionAsync(user.Id, AuthPermissions.UserView)).ShouldBeTrue();
        (await HasPermissionAsync(user.Id, AuthPermissions.AuditView)).ShouldBeTrue();
        (await HasPermissionAsync(user.Id, AuthPermissions.RoleView)).ShouldBeTrue();
        (await HasPermissionAsync(user.Id, AuthPermissions.RoleManage)).ShouldBeFalse();
        (await HasPermissionAsync(user.Id, "AUTH.USER.VIEW")).ShouldBeFalse();
        (await HasPermissionAsync(Guid.NewGuid(), AuthPermissions.UserView)).ShouldBeFalse();
    }

    [Fact]
    public async Task Soft_deleted_role_grants_nothing()
    {
        var readers = await CreateRoleAsync("Readers", AuthPermissions.UserView);
        var managers = await CreateRoleAsync("Managers", AuthPermissions.RoleManage);
        var user = await CreateUserAsync(readers, managers);

        var context = NewScope().GetRequiredService<AuthDbContext>();
        context.Remove(await context.Set<Role>().SingleAsync(role => role.Id == managers.Id, Ct));
        await context.SaveChangesAsync(Ct);

        // The grant and the assignment rows are still there; only the role's IsDeleted flag hides them.
        var grants = await NewScope().GetRequiredService<AuthDbContext>().Set<RolePermission>().CountAsync(grant => grant.RoleId == managers.Id, Ct);
        grants.ShouldBe(1);
        (await HasPermissionAsync(user.Id, AuthPermissions.RoleManage)).ShouldBeFalse();
        (await HasPermissionAsync(user.Id, AuthPermissions.UserView)).ShouldBeTrue();
    }

    [Fact]
    public async Task Soft_deleted_user_has_no_permissions()
    {
        var readers = await CreateRoleAsync("Readers", AuthPermissions.UserView);
        var deleted = await CreateUserAsync(readers);
        var other = await CreateUserAsync(readers);

        var context = NewScope().GetRequiredService<AuthDbContext>();
        context.Remove(await context.Set<User>().SingleAsync(user => user.Id == deleted.Id, Ct));
        await context.SaveChangesAsync(Ct);

        (await HasPermissionAsync(deleted.Id, AuthPermissions.UserView)).ShouldBeFalse();
        (await HasPermissionAsync(other.Id, AuthPermissions.UserView)).ShouldBeTrue();
    }

    [Fact]
    public async Task Suspended_user_has_no_permissions()
    {
        var readers = await CreateRoleAsync("Readers", AuthPermissions.UserView);
        var suspended = await CreateUserAsync(readers);
        var other = await CreateUserAsync(readers);

        var scope = NewScope();
        var user = (await scope.GetRequiredService<IUserRepository>().GetByIdAsync(suspended.Id, Ct)).ShouldNotBeNull();
        user.Suspend(Now);
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        (await HasPermissionAsync(suspended.Id, AuthPermissions.UserView)).ShouldBeFalse();
        (await HasPermissionAsync(other.Id, AuthPermissions.UserView)).ShouldBeTrue();
    }

    [Fact]
    public async Task Second_call_within_thirty_seconds_does_not_query_the_database()
    {
        var readers = await CreateRoleAsync("Readers", AuthPermissions.UserView);
        var user = await CreateUserAsync(readers);
        var (checker, connections) = CreateRecordingChecker();

        (await checker.HasPermissionAsync(user.Id, AuthPermissions.UserView, Ct)).ShouldBeTrue();
        connections.OpenedCount.ShouldBe(1);

        Factory.Time.Advance(CacheLifetime - TimeSpan.FromMilliseconds(1));

        (await checker.HasPermissionAsync(user.Id, AuthPermissions.UserView, Ct)).ShouldBeTrue();
        (await checker.HasPermissionAsync(user.Id, AuthPermissions.RoleManage, Ct)).ShouldBeFalse();
        connections.OpenedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Invalidate_makes_the_next_call_reload()
    {
        var readers = await CreateRoleAsync("Readers", AuthPermissions.UserView);
        var user = await CreateUserAsync(readers);
        var other = await CreateUserAsync(readers);
        var (checker, connections) = CreateRecordingChecker();
        (await checker.HasPermissionAsync(user.Id, AuthPermissions.UserView, Ct)).ShouldBeTrue();
        (await checker.HasPermissionAsync(other.Id, AuthPermissions.UserView, Ct)).ShouldBeTrue();

        await RemoveRoleAsync(user.Id, readers.Id);
        await RemoveRoleAsync(other.Id, readers.Id);

        // Still the cached set: the change is not visible until the key is removed.
        (await checker.HasPermissionAsync(user.Id, AuthPermissions.UserView, Ct)).ShouldBeTrue();
        connections.OpenedCount.ShouldBe(2);

        // The registered cache shares the entries of the checker; only the named user is reloaded.
        await NewScope().GetRequiredService<IPermissionCache>().InvalidateUsersAsync([user.Id], Ct);

        (await checker.HasPermissionAsync(user.Id, AuthPermissions.UserView, Ct)).ShouldBeFalse();
        (await checker.HasPermissionAsync(other.Id, AuthPermissions.UserView, Ct)).ShouldBeTrue();
        connections.OpenedCount.ShouldBe(3);
    }

    [Fact]
    public async Task Result_expires_after_thirty_seconds()
    {
        var readers = await CreateRoleAsync("Readers", AuthPermissions.UserView);
        var user = await CreateUserAsync(readers);
        var (checker, connections) = CreateRecordingChecker();
        (await checker.HasPermissionAsync(user.Id, AuthPermissions.UserView, Ct)).ShouldBeTrue();
        await RemoveRoleAsync(user.Id, readers.Id);

        Factory.Time.Advance(CacheLifetime);

        (await checker.HasPermissionAsync(user.Id, AuthPermissions.UserView, Ct)).ShouldBeFalse();
        connections.OpenedCount.ShouldBe(2);
    }

    [Fact]
    public async Task Reader_returns_the_sorted_set_from_the_entry_the_checker_uses()
    {
        var auditors = await CreateRoleAsync("Auditors", AuthPermissions.UserView, AuthPermissions.AuditView);
        var user = await CreateUserAsync(auditors);
        var (checker, connections) = CreateRecordingChecker();

        (await checker.GetPermissionsAsync(user.Id, Ct)).ShouldBe([AuthPermissions.AuditView, AuthPermissions.UserView]);
        (await checker.HasPermissionAsync(user.Id, AuthPermissions.AuditView, Ct)).ShouldBeTrue();
        connections.OpenedCount.ShouldBe(1);
        (await checker.GetPermissionsAsync(Guid.NewGuid(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public void Registered_checker_reader_and_cache_are_the_permission_checker()
    {
        var scope = NewScope();

        scope.GetRequiredService<IPermissionChecker>().ShouldBeOfType<PermissionChecker>();
        scope.GetRequiredService<IPermissionCache>().ShouldBeSameAs(scope.GetRequiredService<IPermissionChecker>());
        scope.GetRequiredService<IPermissionReader>().ShouldBeSameAs(scope.GetRequiredService<IPermissionChecker>());
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private (PermissionChecker Checker, RecordingDbConnectionFactory Connections) CreateRecordingChecker()
    {
        var connections = new RecordingDbConnectionFactory(Factory.Services.GetRequiredService<IDbConnectionFactory>());
        return (new PermissionChecker(connections, Factory.Services.GetRequiredService<HybridCache>()), connections);
    }

    private Task<bool> HasPermissionAsync(Guid userId, string permission)
        => NewScope().GetRequiredService<IPermissionChecker>().HasPermissionAsync(userId, permission, Ct);

    private async Task<Role> CreateRoleAsync(string name, params string[] permissionCodes)
    {
        var context = NewScope().GetRequiredService<AuthDbContext>();
        var permissionIds = await context.Set<Permission>()
            .Where(permission => permissionCodes.Contains(permission.Code))
            .Select(permission => permission.Id)
            .ToListAsync(Ct);
        permissionIds.Count.ShouldBe(permissionCodes.Length);

        var role = Role.Create(name, $"{name} for the test", Now).Value;
        role.SetPermissions(permissionIds).IsSuccess.ShouldBeTrue();
        context.Add(role);
        await context.SaveChangesAsync(Ct);
        return role;
    }

    private async Task<User> CreateUserAsync(params Role[] roles)
    {
        var user = User.Register($"{Guid.NewGuid():N}@example.com", "Test user", "en", "hash", Now).Value;
        foreach (var role in roles)
        {
            user.AssignRole(role.Id, assignedBy: null, Now);
        }

        var context = NewScope().GetRequiredService<AuthDbContext>();
        context.Add(user);
        await context.SaveChangesAsync(Ct);
        return user;
    }

    private async Task RemoveRoleAsync(Guid userId, Guid roleId)
    {
        var scope = NewScope();
        var user = (await scope.GetRequiredService<IUserRepository>().GetByIdAsync(userId, Ct)).ShouldNotBeNull();
        user.RemoveRole(roleId);
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
    }

    private IServiceProvider NewScope()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider;
    }
}
