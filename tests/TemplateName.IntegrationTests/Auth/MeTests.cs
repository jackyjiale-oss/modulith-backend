using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// <c>GET /api/v1/auth/me</c>: the signed-in user's profile, roles and permissions, read through Dapper, which bypasses the EF Core
/// soft-delete filters, so these tests prove the query repeats them.
/// </summary>
public sealed class MeTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string MeRoute = "/api/v1/auth/me";

    [Fact]
    public async Task Get_me_returns_roles_and_permissions_and_hides_soft_deleted_roles()
    {
        var signedIn = await SignInAsync(AuthPermissions.UserView, AuthPermissions.RoleView);
        var harnessRole = await AddRolesAsync(signedIn.UserId);

        using var response = await Client.GetAsync(MeRoute, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        // Exactly these members: never a hash, a stamp or any token material.
        body.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "email", "emailConfirmed", "displayName", "locale", "timeZone", "roles", "permissions"],
            ignoreOrder: true);
        body.GetProperty("id").GetGuid().ShouldBe(signedIn.UserId);
        body.GetProperty("email").GetString().ShouldBe(signedIn.Email);
        body.GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
        body.GetProperty("displayName").GetString().ShouldBe("Test user");
        body.GetProperty("locale").GetString().ShouldBe("en");
        body.GetProperty("timeZone").GetString().ShouldBe("UTC");

        // The soft-deleted "Archived" role and the permission only it grants are gone; the rows behind them are still there.
        Strings(body.GetProperty("roles")).ShouldBe(new[] { harnessRole, SystemRoles.User }.Order(StringComparer.Ordinal));
        Strings(body.GetProperty("permissions")).ShouldBe([AuthPermissions.RoleView, AuthPermissions.UserView]);
        (await QueryAsync(context => context.Set<Role>().IgnoreQueryFilters().CountAsync(role => role.Name == "Archived" && role.IsDeleted, Ct)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Get_me_without_token_is_401()
    {
        using var response = await Client.GetAsync(MeRoute, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("http.401");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("suspended")]
    [InlineData("deleted")]
    public async Task Get_me_for_a_user_who_cannot_sign_in_any_more_is_401(string state)
    {
        if (state == "unknown")
        {
            AuthTestHarness.Authorize(Client, TestAccessTokens.Issue(Factory.Services).Value);
        }
        else
        {
            var signedIn = await SignInAsync();
            await using var scope = Factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user = await context.Set<User>().SingleAsync(candidate => candidate.Id == signedIn.UserId, Ct);
            if (state == "suspended")
            {
                user.Suspend(Factory.Time.GetUtcNow());
            }
            else
            {
                context.Remove(user);
            }

            await context.SaveChangesAsync(Ct);
        }

        // The token itself is still valid (ADR 0015, D9); the answer is the same 401 as without one.
        using var response = await Client.GetAsync(MeRoute, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("http.401");
    }

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString())];

    /// <summary>Gives the user the seeded <c>User</c> role and an "Archived" role, then soft-deletes "Archived"; returns the harness role's name.</summary>
    private async Task<string> AddRolesAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var now = Factory.Time.GetUtcNow();

        var auditView = await context.Set<Permission>().SingleAsync(permission => permission.Code == AuthPermissions.AuditView, Ct);
        var archived = Role.Create("Archived", "Soft-deleted by the test", now).Value;
        archived.SetPermissions([auditView.Id]).IsSuccess.ShouldBeTrue();
        context.Add(archived);
        var userRole = await context.Set<Role>().SingleAsync(role => role.NormalizedName == Role.NormalizeName(SystemRoles.User), Ct);
        var user = await context.Set<User>().Include(candidate => candidate.Roles).SingleAsync(candidate => candidate.Id == userId, Ct);
        var harnessRoleId = user.Roles.ShouldHaveSingleItem().RoleId;
        user.AssignRole(userRole.Id, assignedBy: null, now);
        user.AssignRole(archived.Id, assignedBy: null, now);
        await context.SaveChangesAsync(Ct);

        context.Remove(archived);
        await context.SaveChangesAsync(Ct);

        return await context.Set<Role>().Where(role => role.Id == harnessRoleId).Select(role => role.Name).SingleAsync(Ct);
    }

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }
}
