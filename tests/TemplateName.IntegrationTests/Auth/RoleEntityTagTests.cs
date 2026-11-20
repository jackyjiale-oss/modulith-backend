using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TemplateName.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.IntegrationTests.Auth;

/// <summary><c>ETag</c> and <c>If-Match</c> on a role and its permissions (ADR 0038): both updates share the role's version.</summary>
public sealed class RoleEntityTagTests(IntegrationTestWebAppFactory factory) : AdminTestBase(factory)
{
    private const string RolesRoute = "/api/v1/admin/auth/roles";

    [Fact]
    public async Task Get_returns_an_etag_that_a_matching_update_accepts_and_replaces()
    {
        var (id, read) = await GivenRoleAsync();

        using var updated = await Client.PutJsonAsync($"{RolesRoute}/{id}", new { name = "Helpdesk", description = "x" }, read, Ct);

        updated.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var next = EntityTagRequests.ETagOf(updated);
        next.ShouldNotBe(read);
        (await ReadETagAsync(id)).ShouldBe(next);
    }

    [Fact]
    public async Task A_stale_etag_is_412_and_changes_nothing()
    {
        var (id, read) = await GivenRoleAsync();
        using (var first = await Client.PutJsonAsync($"{RolesRoute}/{id}", new { name = "First", description = "x" }, read, Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var stale = await Client.PutJsonAsync($"{RolesRoute}/{id}", new { name = "Second", description = "x" }, read, Ct);

        await AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency.precondition_failed");
        stale.Headers.Contains("ETag").ShouldBeFalse();
        (await GetRoleAsync(id)).GetProperty("name").GetString().ShouldBe("First");
    }

    [Fact]
    public async Task Without_if_match_the_update_behaves_as_before()
    {
        var (id, _) = await GivenRoleAsync();

        using var updated = await Client.PutJsonAsync($"{RolesRoute}/{id}", new { name = "Helpdesk", description = "x" }, null, Ct);

        updated.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        EntityTagRequests.ETagOf(updated).ShouldBe(await ReadETagAsync(id));
    }

    [Fact]
    public async Task Star_matches_weak_is_412_and_malformed_is_400()
    {
        var (id, read) = await GivenRoleAsync();

        using (var weak = await Client.PutJsonAsync($"{RolesRoute}/{id}", new { name = "Weak", description = "x" }, "W/" + read, Ct))
        {
            await AssertProblemAsync(weak, HttpStatusCode.PreconditionFailed, "concurrency.precondition_failed");
        }

        using (var malformed = await Client.PutJsonAsync($"{RolesRoute}/{id}", new { name = "Bad", description = "x" }, "not-quoted", Ct))
        {
            await AssertProblemAsync(malformed, HttpStatusCode.BadRequest, "request.malformed");
        }

        using var star = await Client.PutJsonAsync($"{RolesRoute}/{id}", new { name = "Star", description = "x" }, "*", Ct);
        star.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetRoleAsync(id)).GetProperty("name").GetString().ShouldBe("Star");
    }

    [Fact]
    public async Task A_permissions_change_changes_the_role_etag()
    {
        var (id, read) = await GivenRoleAsync();
        var view = await PermissionIdAsync(AuthPermissions.RoleView);

        using var granted = await Client.PutJsonAsync($"{RolesRoute}/{id}/permissions", new { permissionIds = new[] { view } }, read, Ct);

        granted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var next = EntityTagRequests.ETagOf(granted);
        next.ShouldNotBe(read);
        (await ReadETagAsync(id)).ShouldBe(next);
        using var staleRename = await Client.PutJsonAsync($"{RolesRoute}/{id}", new { name = "Helpdesk", description = "x" }, read, Ct);
        await AssertProblemAsync(staleRename, HttpStatusCode.PreconditionFailed, "concurrency.precondition_failed");
    }

    [Fact]
    public async Task A_permissions_update_that_changes_nothing_keeps_the_etag()
    {
        var (id, read) = await GivenRoleAsync();

        using var unchanged = await Client.PutJsonAsync($"{RolesRoute}/{id}/permissions", new { permissionIds = Array.Empty<Guid>() }, read, Ct);

        unchanged.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        EntityTagRequests.ETagOf(unchanged).ShouldBe(read);
    }

    private async Task<(Guid Id, string ETag)> GivenRoleAsync()
    {
        await SignInAsync(AuthPermissions.RoleView, AuthPermissions.RoleManage);
        using var created = await Client.PostAsJsonAsync(RolesRoute, new { name = "Support", description = "Handles tickets" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        return (id, await ReadETagAsync(id));
    }

    private async Task<string> ReadETagAsync(Guid id)
    {
        using var response = await Client.GetAsync($"{RolesRoute}/{id}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return EntityTagRequests.ETagOf(response);
    }

    private async Task<JsonElement> GetRoleAsync(Guid id)
    {
        using var response = await Client.GetAsync($"{RolesRoute}/{id}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task A_permissions_change_bumps_the_role_rowversion_so_a_simultaneous_save_conflicts()
    {
        var (id, read) = await GivenRoleAsync();
        var before = await RowVersionAsync(id);
        var view = await PermissionIdAsync(AuthPermissions.RoleView);

        using var granted = await Client.PutJsonAsync($"{RolesRoute}/{id}/permissions", new { permissionIds = new[] { view } }, read, Ct);

        granted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RowVersionAsync(id)).ShouldNotBe(before);
    }

    private Task<byte[]> RowVersionAsync(Guid id)
        => QueryAsync(context => context.Set<Role>().Where(role => role.Id == id).Select(role => role.RowVersion).SingleAsync(Ct));
}
