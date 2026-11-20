using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.IntegrationTests.Auth;

/// <summary><c>ETag</c> and <c>If-Match</c> on a user's roles (ADR 0038): the version covers the role set only.</summary>
public sealed class UserRolesEntityTagTests(IntegrationTestWebAppFactory factory) : AdminTestBase(factory)
{
    private const string UsersRoute = "/api/v1/admin/auth/users";
    private const string RolesRoute = "/api/v1/admin/auth/roles";
    private const string TargetEmail = "target@example.com";

    [Fact]
    public async Task Get_user_etag_is_accepted_by_assign_roles_and_replaced()
    {
        var (targetId, read) = await GivenTargetWithUserRoleAsync();

        using var assigned = await Client.PutJsonAsync($"{UsersRoute}/{targetId}/roles", new { roleIds = Array.Empty<Guid>() }, read, Ct);

        assigned.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var next = EntityTagRequests.ETagOf(assigned);
        next.ShouldNotBe(read);
        (await ReadETagAsync(targetId)).ShouldBe(next);
    }

    [Fact]
    public async Task A_stale_etag_is_412_and_changes_nothing()
    {
        var (targetId, read) = await GivenTargetWithUserRoleAsync();
        using (var first = await Client.PutJsonAsync($"{UsersRoute}/{targetId}/roles", new { roleIds = Array.Empty<Guid>() }, null, Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var userRoleId = await RoleIdAsync(SystemRoles.User);
        using var stale = await Client.PutJsonAsync($"{UsersRoute}/{targetId}/roles", new { roleIds = new[] { userRoleId } }, read, Ct);

        await AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency.precondition_failed");
        (await GetUserAsync(targetId)).GetProperty("roles").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Signing_in_does_not_change_the_users_etag()
    {
        var (targetId, read) = await GivenTargetWithUserRoleAsync();

        await LoginAsync(TargetEmail, AuthTestHarness.Password);

        (await ReadETagAsync(targetId)).ShouldBe(read);
    }

    [Fact]
    public async Task A_link_to_a_deleted_role_does_not_break_the_precondition()
    {
        var (targetId, _) = await GivenTargetWithUserRoleAsync();
        using var created = await Client.PostAsJsonAsync(RolesRoute, new { name = "Temporary", description = "x" }, Ct);
        var temporaryId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await ChangeUserAsync(targetId, (_, user) => user.AssignRole(temporaryId, targetId, Factory.Time.GetUtcNow()));
        using (var deleted = await Client.DeleteAsync($"{RolesRoute}/{temporaryId}", Ct))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var read = await ReadETagAsync(targetId);
        using var assigned = await Client.PutJsonAsync($"{UsersRoute}/{targetId}/roles", new { roleIds = Array.Empty<Guid>() }, read, Ct);

        assigned.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<(Guid TargetId, string ETag)> GivenTargetWithUserRoleAsync()
    {
        await SignInAsync(AuthPermissions.UserView, AuthPermissions.UserAssignRoles, AuthPermissions.RoleManage);
        var target = await CreateUserAsync(TargetEmail);
        var userRoleId = await RoleIdAsync(SystemRoles.User);
        if (!target.Roles.Any(assignment => assignment.RoleId == userRoleId))
        {
            await ChangeUserAsync(target.Id, (_, user) => user.AssignRole(userRoleId, target.Id, Factory.Time.GetUtcNow()));
        }

        return (target.Id, await ReadETagAsync(target.Id));
    }

    private async Task<string> ReadETagAsync(Guid userId)
    {
        using var response = await Client.GetAsync($"{UsersRoute}/{userId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return EntityTagRequests.ETagOf(response);
    }

    private async Task<JsonElement> GetUserAsync(Guid userId)
    {
        using var response = await Client.GetAsync($"{UsersRoute}/{userId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
