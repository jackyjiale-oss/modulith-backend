using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The role and permission administration routes <c>/api/v1/admin/auth/roles</c> and <c>/permissions</c> through the running host. The
/// lists are read through Dapper, which bypasses the EF Core filters, so these tests also prove its SQL leaves soft-deleted roles out.
/// </summary>
public sealed class AdminRoleTests(IntegrationTestWebAppFactory factory) : AdminTestBase(factory)
{
    private const string RolesRoute = "/api/v1/admin/auth/roles";
    private const string PermissionsRoute = "/api/v1/admin/auth/permissions";
    private const string UsersRoute = "/api/v1/admin/auth/users";
    private const string Password = AuthTestHarness.Password;

    private static readonly PermissionDefinition Widget = new("test.widget.edit", "test", "Edit widgets", "Edit the widgets of the test module.");

    private static readonly string[] AllRolePermissions =
    [
        AuthPermissions.RoleView,
        AuthPermissions.RoleManage,
        AuthPermissions.PermissionView,
    ];

    /// <summary>Every route with the permission it needs and a valid body where it takes one.</summary>
    public static TheoryData<string, string, string, string?> Routes => new()
    {
        { "GET", RolesRoute, AuthPermissions.RoleView, null },
        { "GET", RolesRoute + "/{id}", AuthPermissions.RoleView, null },
        { "POST", RolesRoute, AuthPermissions.RoleManage, """{"name":"Support","description":"Tickets"}""" },
        { "PUT", RolesRoute + "/{id}", AuthPermissions.RoleManage, """{"name":"Helpdesk","description":"Tickets"}""" },
        { "DELETE", RolesRoute + "/{id}", AuthPermissions.RoleManage, null },
        { "PUT", RolesRoute + "/{id}/permissions", AuthPermissions.RoleManage, """{"permissionIds":[]}""" },
        { "GET", PermissionsRoute, AuthPermissions.PermissionView, null },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Anonymous_is_401_and_signed_in_without_permission_is_403(string method, string route, string permission, string? body)
    {
        var role = await CreateRoleDirectAsync("Support");
        var path = route.Replace("{id}", role.ToString(), StringComparison.Ordinal);

        using (var anonymous = await SendAsync(method, path, body))
        {
            await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "http.401");
        }

        // Every other role and permission administration permission, but not this route's.
        await SignInAsync([.. AllRolePermissions.Where(code => code != permission)], AuthTestHarness.DefaultLocale);
        using (var forbidden = await SendAsync(method, path, body))
        {
            await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "http.403");
        }

        // Nothing happened to the role, and nothing was audited.
        var stored = await QueryAsync(context => context.Set<Role>().SingleAsync(candidate => candidate.Id == role, Ct));
        stored.Name.ShouldBe("Support");
        (await RoleAuditRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Role_crud_round_trip()
    {
        var actor = await SignInAsync(AuthPermissions.RoleView, AuthPermissions.RoleManage);

        using var created = await Client.PostAsJsonAsync(RolesRoute, new { name = "Support", description = "Handles tickets" }, Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        created.Headers.Location!.OriginalString.ShouldBe($"{RolesRoute}/{id}");

        var read = await GetRoleAsync(id);
        read.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "name", "description", "isSystem", "createdAt", "permissions"],
            ignoreOrder: true);
        read.GetProperty("name").GetString().ShouldBe("Support");
        read.GetProperty("description").GetString().ShouldBe("Handles tickets");
        read.GetProperty("isSystem").GetBoolean().ShouldBeFalse();
        read.GetProperty("permissions").GetArrayLength().ShouldBe(0);

        using (var updated = await Client.PutAsJsonAsync($"{RolesRoute}/{id}", new { name = "Helpdesk", description = "Answers tickets" }, Ct))
        {
            updated.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var renamed = await GetRoleAsync(id);
        renamed.GetProperty("name").GetString().ShouldBe("Helpdesk");
        renamed.GetProperty("description").GetString().ShouldBe("Answers tickets");
        Names(await GetPageAsync(RolesRoute, "?includeTotalCount=true")).ShouldContain("Helpdesk");
        Names(await GetPageAsync(RolesRoute, string.Empty)).ShouldNotContain("Support");

        using (var deleted = await Client.DeleteAsync($"{RolesRoute}/{id}", Ct))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var gone = await Client.GetAsync($"{RolesRoute}/{id}", Ct))
        {
            await AssertProblemAsync(gone, HttpStatusCode.NotFound, "auth.role_not_found");
        }

        Names(await GetPageAsync(RolesRoute, string.Empty)).ShouldNotContain("Helpdesk");

        // Deleting it again, or changing it, is the same 404.
        using (var again = await Client.DeleteAsync($"{RolesRoute}/{id}", Ct))
        {
            await AssertProblemAsync(again, HttpStatusCode.NotFound, "auth.role_not_found");
        }

        using (var changed = await Client.PutAsJsonAsync($"{RolesRoute}/{id}", new { name = "Again", description = "x" }, Ct))
        {
            await AssertProblemAsync(changed, HttpStatusCode.NotFound, "auth.role_not_found");
        }

        var rows = await RoleAuditRowsAsync();
        rows.Select(row => row.EventType).ShouldBe([AuthAuditEvents.RoleCreated, AuthAuditEvents.RoleUpdated, AuthAuditEvents.RoleDeleted]);
        foreach (var row in rows)
        {
            row.Succeeded.ShouldBeTrue();
            row.UserId.ShouldBeNull();
            row.TraceId.ShouldNotBeNull();
            using var details = JsonDocument.Parse(row.Details!);
            details.RootElement.GetProperty("actorId").GetGuid().ShouldBe(actor.UserId);
            details.RootElement.GetProperty("roleId").GetGuid().ShouldBe(id);
        }
    }

    [Fact]
    public async Task Role_is_validated()
    {
        await SignInAsync(AuthPermissions.RoleView, AuthPermissions.RoleManage);
        var id = await CreateRoleDirectAsync("Support");

        foreach (var body in new object[]
        {
            new { name = string.Empty, description = "x" },
            new { name = "   ", description = "x" },
            new { name = new string('n', Role.MaxNameLength + 1), description = "x" },
            new { name = "Valid", description = new string('d', Role.MaxDescriptionLength + 1) },
            new { name = "Valid" },
        })
        {
            using var create = await Client.PostAsJsonAsync(RolesRoute, body, Ct);
            await AssertProblemAsync(create, HttpStatusCode.BadRequest, "validation.failed");
            using var update = await Client.PutAsJsonAsync($"{RolesRoute}/{id}", body, Ct);
            await AssertProblemAsync(update, HttpStatusCode.BadRequest, "validation.failed");
        }

        // The longest values and an empty description are fine.
        using var longest = await Client.PostAsJsonAsync(
            RolesRoute,
            new { name = new string('n', Role.MaxNameLength), description = string.Empty },
            Ct);
        longest.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await RoleAuditRowsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Role_name_is_unique_ignoring_case()
    {
        await SignInAsync(AuthPermissions.RoleView, AuthPermissions.RoleManage);
        using (var first = await Client.PostAsJsonAsync(RolesRoute, new { name = "Support", description = "x" }, Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        foreach (var name in new[] { "SUPPORT", "support", " Support ", "sUpPoRt" })
        {
            using var duplicate = await Client.PostAsJsonAsync(RolesRoute, new { name, description = "y" }, Ct);
            await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "auth.role_name_taken");
        }

        // A system role's name is taken too.
        using (var system = await Client.PostAsJsonAsync(RolesRoute, new { name = "superadmin", description = "y" }, Ct))
        {
            await AssertProblemAsync(system, HttpStatusCode.Conflict, "auth.role_name_taken");
        }

        // Renaming another role onto it fails; renaming a role to its own name in another case is fine.
        var other = await CreateRoleViaApiAsync("Auditors");
        using (var clash = await Client.PutAsJsonAsync($"{RolesRoute}/{other}", new { name = "SUPPORT", description = "y" }, Ct))
        {
            await AssertProblemAsync(clash, HttpStatusCode.Conflict, "auth.role_name_taken");
        }

        using (var sameName = await Client.PutAsJsonAsync($"{RolesRoute}/{other}", new { name = "AUDITORS", description = "y" }, Ct))
        {
            sameName.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The refused requests wrote nothing: two creates and the one rename.
        (await RoleAuditRowsAsync()).Select(row => row.EventType)
            .ShouldBe([AuthAuditEvents.RoleCreated, AuthAuditEvents.RoleCreated, AuthAuditEvents.RoleUpdated]);

        // A deleted role frees its name.
        using (var deleted = await Client.DeleteAsync($"{RolesRoute}/{other}", Ct))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var reused = await Client.PostAsJsonAsync(RolesRoute, new { name = "auditors", description = "again" }, Ct);
        reused.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Concurrent_creates_of_one_name_yield_one_role_and_never_a_500()
    {
        await SignInAsync(AuthPermissions.RoleManage);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Client.PostAsJsonAsync(RolesRoute, new { name = "Racers", description = "x" }, Ct)));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(7);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        (await QueryAsync(context => context.Set<Role>().CountAsync(role => role.NormalizedName == "RACERS", Ct))).ShouldBe(1);
        (await RoleAuditRowsAsync()).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(SystemRoles.SuperAdmin)]
    [InlineData(SystemRoles.Admin)]
    [InlineData(SystemRoles.User)]
    public async Task System_roles_cannot_be_renamed_or_deleted(string name)
    {
        await SignInAsync(AuthPermissions.RoleView, AuthPermissions.RoleManage);
        var id = await RoleIdAsync(name);

        using (var rename = await Client.PutAsJsonAsync($"{RolesRoute}/{id}", new { name = "Renamed", description = "x" }, Ct))
        {
            await AssertProblemAsync(rename, HttpStatusCode.Conflict, "auth.system_role_protected");
        }

        using (var delete = await Client.DeleteAsync($"{RolesRoute}/{id}", Ct))
        {
            await AssertProblemAsync(delete, HttpStatusCode.Conflict, "auth.system_role_protected");
        }

        var role = await GetRoleAsync(id);
        role.GetProperty("name").GetString().ShouldBe(name);
        role.GetProperty("isSystem").GetBoolean().ShouldBeTrue();
        (await RoleAuditRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task SuperAdmin_permissions_cannot_be_edited_but_Admin_permissions_can()
    {
        // The caller holds every permission it is about to grant (an actor cannot grant what it does not hold).
        await SignInAsync(AuthPermissions.RoleView, AuthPermissions.RoleManage, AuthPermissions.UserView, AuthPermissions.UserLock);
        var superAdminId = await RoleIdAsync(SystemRoles.SuperAdmin);
        var adminId = await RoleIdAsync(SystemRoles.Admin);
        var view = await PermissionIdAsync(AuthPermissions.UserView);
        var lockUsers = await PermissionIdAsync(AuthPermissions.UserLock);
        var superAdminBefore = await GrantedCodesAsync(superAdminId);
        superAdminBefore.ShouldContain(AuthPermissions.RoleManage);

        using (var refused = await Client.PutAsJsonAsync($"{RolesRoute}/{superAdminId}/permissions", new { permissionIds = new[] { view } }, Ct))
        {
            await AssertProblemAsync(refused, HttpStatusCode.Conflict, "auth.system_role_protected");
        }

        (await GrantedCodesAsync(superAdminId)).ShouldBe(superAdminBefore, ignoreOrder: true);

        using (var changed = await Client.PutAsJsonAsync($"{RolesRoute}/{adminId}/permissions", new { permissionIds = new[] { view, lockUsers } }, Ct))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await GrantedCodesAsync(adminId)).ShouldBe([AuthPermissions.UserView, AuthPermissions.UserLock], ignoreOrder: true);
        var read = await GetRoleAsync(adminId);
        read.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetProperty("code").GetString())
            .ShouldBe([AuthPermissions.UserView, AuthPermissions.UserLock], ignoreOrder: true);

        // The seeder keeps the administrators' choice on the next start.
        await Factory.Services.SeedAuthModuleAsync(Ct);
        (await GrantedCodesAsync(adminId)).ShouldBe([AuthPermissions.UserView, AuthPermissions.UserLock], ignoreOrder: true);
    }

    [Fact]
    public async Task Revoking_a_role_permission_takes_effect_on_the_next_request()
    {
        await SignInAsync(AuthPermissions.RoleManage, AuthPermissions.RoleView, AuthPermissions.UserView);
        var roleId = await CreateRoleViaApiAsync("Support");
        var userView = await PermissionIdAsync(AuthPermissions.UserView);
        using (var granted = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { userView } }, Ct))
        {
            granted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var target = await CreateUserAsync("agent@example.com");
        await GrantRoleAsync(target.Id, roleId);
        var tokens = await LoginAsync("agent@example.com", Password);

        // The agent's permission set is now cached for 30 seconds.
        using (var before = await GetAsAsync(tokens.AccessToken, UsersRoute))
        {
            before.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var revoked = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = Array.Empty<Guid>() }, Ct))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // No clock movement: the change removed the cached set, so the very next request is refused.
        using (var after = await GetAsAsync(tokens.AccessToken, UsersRoute))
        {
            after.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // Granting it again works the same way.
        using (var regranted = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { userView } }, Ct))
        {
            regranted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var restored = await GetAsAsync(tokens.AccessToken, UsersRoute);
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deleting_a_role_removes_its_grants_and_hides_it_from_lists()
    {
        await SignInAsync(AuthPermissions.RoleManage, AuthPermissions.RoleView, AuthPermissions.UserView);
        await CreateRoleViaApiAsync("Kept");
        var roleId = await CreateRoleViaApiAsync("Support");
        var userView = await PermissionIdAsync(AuthPermissions.UserView);
        using (var granted = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { userView } }, Ct))
        {
            granted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var target = await CreateUserAsync("agent@example.com");
        await GrantRoleAsync(target.Id, roleId);
        var tokens = await LoginAsync("agent@example.com", Password);
        using (var before = await GetAsAsync(tokens.AccessToken, UsersRoute))
        {
            before.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var deleted = await Client.DeleteAsync($"{RolesRoute}/{roleId}", Ct))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The grants are gone; the row is only soft-deleted (a hard delete would prove nothing about the Dapper filter).
        (await QueryAsync(context => context.Set<Role>().IgnoreQueryFilters().CountAsync(role => role.Id == roleId && role.IsDeleted, Ct))).ShouldBe(1);
        (await QueryAsync(context => context.Set<RolePermission>().CountAsync(grant => grant.RoleId == roleId, Ct))).ShouldBe(0);

        // Hidden from the list, its count and the read, while the other role stays.
        var page = await GetPageAsync(RolesRoute, "?includeTotalCount=true&pageSize=100");
        Names(page).ShouldContain("Kept");
        Names(page).ShouldNotContain("Support");
        page.GetProperty("totalCount").GetInt64().ShouldBe(Names(page).Count);
        (await GetPageAsync(RolesRoute, "?sort=createdAt&pageSize=100")).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ShouldNotContain(roleId);
        using (var gone = await Client.GetAsync($"{RolesRoute}/{roleId}", Ct))
        {
            await AssertProblemAsync(gone, HttpStatusCode.NotFound, "auth.role_not_found");
        }

        // The user loses the permission on the next request, and the role is not listed among the user's roles.
        using (var after = await GetAsAsync(tokens.AccessToken, UsersRoute))
        {
            after.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var user = await Client.GetAsync($"{UsersRoute}/{target.Id}", Ct))
        {
            var body = await user.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("roles").EnumerateArray().Select(role => role.GetProperty("name").GetString()).ShouldBe([SystemRoles.User]);
        }

        // The name is free again, for a role without the old grants.
        var reborn = await CreateRoleViaApiAsync("Support");
        (await GetRoleAsync(reborn)).GetProperty("permissions").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Setting_permissions_checks_the_ids_and_what_the_caller_holds()
    {
        // The caller holds view but not lock.
        await SignInAsync(AuthPermissions.RoleManage, AuthPermissions.UserView);
        var roleId = await CreateRoleViaApiAsync("Support");
        var view = await PermissionIdAsync(AuthPermissions.UserView);
        var lockUsers = await PermissionIdAsync(AuthPermissions.UserLock);

        using (var unknown = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { view, Guid.NewGuid() } }, Ct))
        {
            await AssertProblemAsync(unknown, HttpStatusCode.BadRequest, "auth.permission_not_found");
        }

        using (var notHeld = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { view, lockUsers } }, Ct))
        {
            await AssertProblemAsync(notHeld, HttpStatusCode.Forbidden, "auth.permission_grant_not_allowed");
        }

        using (var empty = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { Guid.Empty } }, Ct))
        {
            await AssertProblemAsync(empty, HttpStatusCode.BadRequest, "validation.failed");
        }

        using (var missing = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { }, Ct))
        {
            await AssertProblemAsync(missing, HttpStatusCode.BadRequest, "validation.failed");
        }

        using (var unknownRole = await Client.PutAsJsonAsync($"{RolesRoute}/{Guid.NewGuid()}/permissions", new { permissionIds = new[] { view } }, Ct))
        {
            await AssertProblemAsync(unknownRole, HttpStatusCode.NotFound, "auth.role_not_found");
        }

        (await GrantedCodesAsync(roleId)).ShouldBeEmpty();

        // What the caller does hold is granted, duplicates ignored.
        using (var granted = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { view, view } }, Ct))
        {
            granted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await GrantedCodesAsync(roleId)).ShouldBe([AuthPermissions.UserView]);
    }

    [Fact]
    public async Task A_deprecated_permission_cannot_be_newly_granted_but_a_granted_one_may_stay()
    {
        Factory.PermissionSource.Declare(Widget);
        await Factory.Services.SeedAuthModuleAsync(Ct);
        await SignInAsync(AuthPermissions.RoleView, AuthPermissions.RoleManage, AuthPermissions.UserView, Widget.Code);
        var roleId = await CreateRoleViaApiAsync("Support");
        var widget = await PermissionIdAsync(Widget.Code);
        var view = await PermissionIdAsync(AuthPermissions.UserView);
        using (var granted = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { widget } }, Ct))
        {
            granted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The module drops the permission.
        Factory.PermissionSource.Reset();
        await Factory.Services.SeedAuthModuleAsync(Ct);

        using (var kept = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { widget, view } }, Ct))
        {
            kept.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var other = await CreateRoleViaApiAsync("Other");
        using (var refused = await Client.PutAsJsonAsync($"{RolesRoute}/{other}/permissions", new { permissionIds = new[] { widget } }, Ct))
        {
            await AssertProblemAsync(refused, HttpStatusCode.BadRequest, "auth.permission_not_found");
        }

        (await GrantedCodesAsync(roleId)).ShouldBe([Widget.Code, AuthPermissions.UserView], ignoreOrder: true);
        var read = await GetRoleAsync(roleId);
        read.GetProperty("permissions").EnumerateArray()
            .Single(permission => permission.GetProperty("code").GetString() == Widget.Code)
            .GetProperty("isDeprecated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Role_list_is_paged_sorted_and_counts_the_grants()
    {
        await SignInAsync(AuthPermissions.RoleView, AuthPermissions.RoleManage, AuthPermissions.UserView);
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        var zebra = await CreateRoleViaApiAsync("Zebra");
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        var apple = await CreateRoleViaApiAsync("apple");
        var view = await PermissionIdAsync(AuthPermissions.UserView);
        using (var granted = await Client.PutAsJsonAsync($"{RolesRoute}/{apple}/permissions", new { permissionIds = new[] { view } }, Ct))
        {
            granted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The default order is the name, case-insensitively. The signed-in caller's own role (made by the harness) is listed too.
        var own = await QueryAsync(context => context.Set<Role>().Where(role => role.Name.StartsWith("Test role")).Select(role => role.Name).SingleAsync(Ct));
        var all = await GetPageAsync(RolesRoute, "?includeTotalCount=true");
        Names(all).ShouldBe([SystemRoles.Admin, "apple", SystemRoles.SuperAdmin, own, SystemRoles.User, "Zebra"]);
        all.GetProperty("totalCount").GetInt64().ShouldBe(6);
        var items = all.GetProperty("items").EnumerateArray().ToList();
        items[0].EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "name", "description", "isSystem", "permissionCount", "createdAt"],
            ignoreOrder: true);
        items.Single(item => item.GetProperty("name").GetString() == "apple").GetProperty("permissionCount").GetInt32().ShouldBe(1);
        items.Single(item => item.GetProperty("name").GetString() == "Zebra").GetProperty("permissionCount").GetInt32().ShouldBe(0);
        items.Single(item => item.GetProperty("name").GetString() == SystemRoles.SuperAdmin).GetProperty("isSystem").GetBoolean().ShouldBeTrue();
        var activePermissions = await QueryAsync(context => context.Set<Permission>().CountAsync(permission => !permission.IsDeprecated, Ct));
        items.Single(item => item.GetProperty("name").GetString() == SystemRoles.SuperAdmin).GetProperty("permissionCount").GetInt32()
            .ShouldBe(activePermissions);

        // Two per page, by name.
        var first = await GetPageAsync(RolesRoute, "?pageSize=2");
        Names(first).ShouldBe([SystemRoles.Admin, "apple"]);
        var second = await GetPageAsync(RolesRoute, $"?pageSize=2&cursor={first.GetProperty("nextCursor").GetString()}");
        Names(second).ShouldBe([SystemRoles.SuperAdmin, own]);

        // Newest first by creation time, then back with the previous cursor.
        var newest = await GetPageAsync(RolesRoute, "?sort=-createdAt&pageSize=2");
        newest.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([apple, zebra]);

        using (var badSort = await Client.GetAsync($"{RolesRoute}?sort=description", Ct))
        {
            await AssertProblemAsync(badSort, HttpStatusCode.BadRequest, "pagination.invalid_sort");
        }

        using (var mismatch = await Client.GetAsync($"{RolesRoute}?sort=createdAt&cursor={first.GetProperty("nextCursor").GetString()}", Ct))
        {
            await AssertProblemAsync(mismatch, HttpStatusCode.BadRequest, "pagination.cursor_mismatch");
        }

        using (var tooBig = await Client.GetAsync($"{RolesRoute}?pageSize=101", Ct))
        {
            await AssertProblemAsync(tooBig, HttpStatusCode.BadRequest, "validation.failed");
        }

        using var unknown = await Client.GetAsync($"{RolesRoute}/{Guid.NewGuid()}", Ct);
        await AssertProblemAsync(unknown, HttpStatusCode.NotFound, "auth.role_not_found");
    }

    [Fact]
    public async Task Permissions_list_hides_deprecated_by_default()
    {
        Factory.PermissionSource.Declare(Widget);
        await Factory.Services.SeedAuthModuleAsync(Ct);
        await SignInAsync(AuthPermissions.PermissionView);
        Factory.PermissionSource.Reset();
        await Factory.Services.SeedAuthModuleAsync(Ct);

        var visible = await GetPageAsync(PermissionsRoute, "?pageSize=100&includeTotalCount=true");
        var codes = Codes(visible);
        codes.ShouldNotContain(Widget.Code);
        codes.ShouldContain(AuthPermissions.RoleManage);
        codes.ShouldBe([.. codes.Order(StringComparer.Ordinal)]);
        visible.GetProperty("totalCount").GetInt64().ShouldBe(codes.Count);
        var item = visible.GetProperty("items").EnumerateArray().First();
        item.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "code", "module", "name", "description", "isDeprecated"],
            ignoreOrder: true);
        item.GetProperty("isDeprecated").GetBoolean().ShouldBeFalse();

        var everything = await GetPageAsync(PermissionsRoute, "?pageSize=100&includeDeprecated=true&includeTotalCount=true");
        Codes(everything).ShouldContain(Widget.Code);
        Codes(everything).Count.ShouldBe(codes.Count + 1);
        everything.GetProperty("totalCount").GetInt64().ShouldBe(codes.Count + 1);
        everything.GetProperty("items").EnumerateArray().Single(permission => permission.GetProperty("code").GetString() == Widget.Code)
            .GetProperty("isDeprecated").GetBoolean().ShouldBeTrue();

        // Paged by code with a cursor, descending on request; a cursor belongs to its filters.
        var firstPage = await GetPageAsync(PermissionsRoute, "?pageSize=3");
        Codes(firstPage).ShouldBe(codes.Take(3));
        var secondPage = await GetPageAsync(PermissionsRoute, $"?pageSize=3&cursor={firstPage.GetProperty("nextCursor").GetString()}");
        Codes(secondPage).ShouldBe(codes.Skip(3).Take(3));
        Codes(await GetPageAsync(PermissionsRoute, "?pageSize=2&sort=-code")).ShouldBe(codes.AsEnumerable().Reverse().Take(2));
        using (var mismatch = await Client.GetAsync($"{PermissionsRoute}?includeDeprecated=true&pageSize=3&cursor={firstPage.GetProperty("nextCursor").GetString()}", Ct))
        {
            await AssertProblemAsync(mismatch, HttpStatusCode.BadRequest, "pagination.cursor_mismatch");
        }

        using var badSort = await Client.GetAsync($"{PermissionsRoute}?sort=name", Ct);
        await AssertProblemAsync(badSort, HttpStatusCode.BadRequest, "pagination.invalid_sort");
    }

    [Fact]
    public async Task Audit_rows_record_the_actor_the_role_and_the_permission_codes()
    {
        var actor = await SignInAsync(AuthPermissions.RoleManage, AuthPermissions.UserView, AuthPermissions.UserLock);
        var roleId = await CreateRoleViaApiAsync("Support");
        var view = await PermissionIdAsync(AuthPermissions.UserView);
        var lockUsers = await PermissionIdAsync(AuthPermissions.UserLock);

        using (var first = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { view } }, Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var second = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { lockUsers } }, Ct))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The same set again changes nothing and is not audited.
        using (var same = await Client.PutAsJsonAsync($"{RolesRoute}/{roleId}/permissions", new { permissionIds = new[] { lockUsers } }, Ct))
        {
            same.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var rows = await RoleAuditRowsAsync();
        rows.Select(row => row.EventType).ShouldBe([AuthAuditEvents.RoleCreated, AuthAuditEvents.RolePermissionsChanged, AuthAuditEvents.RolePermissionsChanged]);
        using var firstDetails = JsonDocument.Parse(rows[1].Details!);
        firstDetails.RootElement.GetProperty("actorId").GetGuid().ShouldBe(actor.UserId);
        firstDetails.RootElement.GetProperty("roleId").GetGuid().ShouldBe(roleId);
        firstDetails.RootElement.GetProperty("added").EnumerateArray().Select(code => code.GetString()).ShouldBe([AuthPermissions.UserView]);
        firstDetails.RootElement.GetProperty("removed").GetArrayLength().ShouldBe(0);
        using var secondDetails = JsonDocument.Parse(rows[2].Details!);
        secondDetails.RootElement.GetProperty("added").EnumerateArray().Select(code => code.GetString()).ShouldBe([AuthPermissions.UserLock]);
        secondDetails.RootElement.GetProperty("removed").EnumerateArray().Select(code => code.GetString()).ShouldBe([AuthPermissions.UserView]);
    }

    private static List<string> Names(JsonElement page)
        => [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];

    private static List<string> Codes(JsonElement page)
        => [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("code").GetString()!)];

    private async Task<JsonElement> GetPageAsync(string route, string query)
    {
        using var response = await Client.GetAsync(route + query, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<JsonElement> GetRoleAsync(Guid id)
    {
        using var response = await Client.GetAsync($"{RolesRoute}/{id}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<Guid> CreateRoleViaApiAsync(string name)
    {
        using var response = await Client.PostAsJsonAsync(RolesRoute, new { name, description = $"The {name} role." }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateRoleDirectAsync(string name)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var role = Role.Create(name, $"The {name} role.", Factory.Time.GetUtcNow()).Value;
        context.Add(role);
        await context.SaveChangesAsync(Ct);
        return role.Id;
    }

    private Task<List<string>> GrantedCodesAsync(Guid roleId)
        => QueryAsync(async context => await context.Set<RolePermission>()
            .Where(grant => grant.RoleId == roleId)
            .Join(context.Set<Permission>(), grant => grant.PermissionId, permission => permission.Id, (_, permission) => permission.Code)
            .ToListAsync(Ct));

    private Task<List<AuthAuditLog>> RoleAuditRowsAsync()
        => QueryAsync(context => context.Set<AuthAuditLog>()
            .Where(entry => entry.EventType.StartsWith("auth.role_"))
            .OrderBy(entry => entry.Id)
            .ToListAsync(Ct));
}
