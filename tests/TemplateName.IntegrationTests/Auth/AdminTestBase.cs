using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// What the administration tests share: problem assertions, anonymous and token-specific requests, a real sign-in through the login
/// endpoint, direct database reads, and role assignment by id or by name.
/// </summary>
public abstract class AdminTestBase(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    internal const string LoginRoute = "/api/v1/auth/login";

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe(code);
    }

    /// <summary>Sends <paramref name="method"/> to <paramref name="path"/> with the signed-in client and an optional JSON text body.</summary>
    internal async Task<HttpResponseMessage> SendAsync(string method, string path, string? body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await Client.SendAsync(request, Ct);
    }

    internal async Task<HttpResponseMessage> PostAnonymousAsync(string route, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        using var anonymous = Factory.CreateClient();
        return await anonymous.SendAsync(request, Ct);
    }

    /// <summary>A GET with <paramref name="accessToken"/> instead of the signed-in client's token.</summary>
    internal async Task<HttpResponseMessage> GetAsAsync(string accessToken, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route).WithBearer(accessToken);
        return await Client.SendAsync(request, Ct);
    }

    /// <summary>Signs a user (created with <see cref="IntegrationTestBase.CreateUserAsync"/>) in through the login endpoint.</summary>
    internal async Task<Tokens> LoginAsync(string email, string password)
    {
        using var response = await PostAnonymousAsync(LoginRoute, new { email, password });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!, body.GetProperty("sessionId").GetGuid());
    }

    internal Task<Guid> RoleIdAsync(string name)
        => QueryAsync(context => context.Set<Role>().Where(role => role.NormalizedName == Role.NormalizeName(name)).Select(role => role.Id).SingleAsync(Ct));

    internal Task<Guid> PermissionIdAsync(string code)
        => QueryAsync(context => context.Set<Permission>().Where(permission => permission.Code == code).Select(permission => permission.Id).SingleAsync(Ct));

    internal async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    /// <summary>Changes a user (loaded with its roles) through the context, outside the API.</summary>
    internal async Task ChangeUserAsync(Guid userId, Action<AuthDbContext, User> change)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await context.Set<User>().Include(candidate => candidate.Roles).SingleAsync(candidate => candidate.Id == userId, Ct);
        change(context, user);
        await context.SaveChangesAsync(Ct);
    }

    internal async Task GrantRoleAsync(Guid userId, string roleName) => await GrantRoleAsync(userId, await RoleIdAsync(roleName));

    internal Task GrantRoleAsync(Guid userId, Guid roleId)
        => ChangeUserAsync(userId, (_, user) => user.AssignRole(roleId, assignedBy: null, Factory.Time.GetUtcNow()));

    internal sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);
}
