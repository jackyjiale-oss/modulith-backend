using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// <c>GET /api/v1/auth/sessions</c> and <c>DELETE /api/v1/auth/sessions/{id}</c> through the running host. The list is read through
/// Dapper, which bypasses the EF Core filters, so these tests prove its SQL keeps to the caller's own, active sessions.
/// </summary>
public sealed class SessionTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string LoginRoute = "/api/v1/auth/login";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string SessionsRoute = "/api/v1/auth/sessions";
    private const string AliceEmail = "alice@example.com";
    private const string BobEmail = "bob@example.com";

    [Fact]
    public async Task Lists_only_my_active_sessions_and_flags_the_current_one()
    {
        await CreateUserAsync(AliceEmail);
        await CreateUserAsync(BobEmail);
        var loggedInAt = Factory.Time.GetUtcNow();
        var laptop = await LoginAsync(AliceEmail, "Laptop", "203.0.113.10");
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var phone = await LoginAsync(AliceEmail, "Phone", "203.0.113.11");
        await LoginAsync(BobEmail, "Bob's laptop", "203.0.113.12");

        using var response = await GetSessionsAsync(laptop.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var items = page.GetProperty("items").EnumerateArray().ToList();

        // Newest activity first; Bob's session is not Alice's to see.
        items.Select(item => item.GetProperty("id").GetGuid()).ShouldBe([phone.SessionId, laptop.SessionId]);
        items.Select(item => item.GetProperty("isCurrent").GetBoolean()).ShouldBe([false, true]);
        items.Select(item => item.GetProperty("deviceName").GetString()).ShouldBe(["Phone", "Laptop"]);
        items.Select(item => item.GetProperty("ipAddress").GetString()).ShouldBe(["203.0.113.11", "203.0.113.10"]);
        items[1].GetProperty("createdAt").GetDateTimeOffset().ShouldBe(loggedInAt);
        items[1].GetProperty("lastSeenAt").GetDateTimeOffset().ShouldBe(loggedInAt);
        items[1].GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(loggedInAt.AddDays(90));
        page.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        // Exactly these members: never a token, a hash or the security stamp.
        items[0].EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "deviceName", "ipAddress", "userAgent", "createdAt", "lastSeenAt", "expiresAt", "isCurrent"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Sessions_are_paged_by_cursor()
    {
        await CreateUserAsync(AliceEmail);

        // Five sessions, two pairs sharing their instant: the cursor must break the ties on the id and neither skip nor repeat a row.
        var created = new List<Tokens>();
        for (var index = 0; index < 5; index++)
        {
            created.Add(await LoginAsync(AliceEmail, $"Device {index}", "203.0.113.10"));
            if (index is 1 or 3)
            {
                Factory.Time.Advance(TimeSpan.FromMinutes(1));
            }
        }

        var caller = created[^1].AccessToken;
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var query = $"pageSize=2&sort=-lastSeenAt" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var response = await GetSessionsAsync(caller, query);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            page.GetProperty("pageSize").GetInt32().ShouldBe(2);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.ShouldBe(3);
        seen.Count.ShouldBe(5);
        seen.Distinct().Count().ShouldBe(5);
        seen.ShouldBe(created.Select(tokens => tokens.SessionId), ignoreOrder: true);

        // lastSeenAt descending, the id descending within a tie: the order SQL Server itself gives (its Guid order is not .NET's).
        var expected = await QueryAsync(context => context.Set<UserSession>()
            .OrderByDescending(session => session.LastSeenAt).ThenByDescending(session => session.Id)
            .Select(session => session.Id).ToListAsync(Ct));
        seen.ShouldBe(expected);

        // The same list the other way round, and a sort field that is not allowed.
        using var oldestFirst = await GetSessionsAsync(caller, "sort=createdAt&pageSize=100");
        var ascending = (await oldestFirst.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        ascending.ShouldBe(ascending.Order().ToList());
        using var invalidSort = await GetSessionsAsync(caller, "sort=deviceName");
        invalidSort.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalidSort.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("pagination.invalid_sort");
    }

    [Fact]
    public async Task Revoked_and_expired_sessions_are_not_listed()
    {
        await CreateUserAsync(AliceEmail);
        var current = await LoginAsync(AliceEmail, "Current", "203.0.113.10");
        var refreshed = await LoginAsync(AliceEmail, "Refreshed", "203.0.113.10");
        var revoked = await LoginAsync(AliceEmail, "Revoked", "203.0.113.10");
        var absoluteExpired = await LoginAsync(AliceEmail, "Past absolute expiry", "203.0.113.10");
        var slidingExpired = await LoginAsync(AliceEmail, "Idle too long", "203.0.113.10");
        var expiringNow = await LoginAsync(AliceEmail, "Expires this instant", "203.0.113.10");

        // A rotated session stays listed: its old token is used, its new one is live.
        using (var refresh = await PostRefreshAsync(refreshed.RefreshToken))
        {
            refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var revoke = await DeleteSessionAsync(current.AccessToken, revoked.SessionId))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var now = Factory.Time.GetUtcNow().UtcDateTime;
        await ExecuteAsync(context => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE auth.UserSessions SET ExpiresAt = {now.AddMinutes(-1)} WHERE Id = {absoluteExpired.SessionId}", Ct));
        await ExecuteAsync(context => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE auth.RefreshTokens SET ExpiresAt = {now.AddMinutes(-1)} WHERE SessionId = {slidingExpired.SessionId}", Ct));
        await ExecuteAsync(context => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE auth.UserSessions SET ExpiresAt = {now} WHERE Id = {expiringNow.SessionId}", Ct));

        using var response = await GetSessionsAsync(current.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ids = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToList();
        ids.ShouldBe([current.SessionId, refreshed.SessionId], ignoreOrder: true);
    }

    [Fact]
    public async Task Revoking_a_session_stops_its_refresh_token()
    {
        await CreateUserAsync(AliceEmail);
        var laptop = await LoginAsync(AliceEmail, "Laptop", "203.0.113.10");
        var phone = await LoginAsync(AliceEmail, "Phone", "203.0.113.11");

        using (var revoke = await DeleteSessionAsync(laptop.AccessToken, phone.SessionId))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Stored: revoked with every token, audited.
        var session = await QueryAsync(context => context.Set<UserSession>().Include(candidate => candidate.RefreshTokens)
            .SingleAsync(candidate => candidate.Id == phone.SessionId, Ct));
        session.RevokedAt.ShouldBe(Factory.Time.GetUtcNow());
        session.RevokedReason.ShouldBe(SessionRevokedReason.Logout);
        session.RefreshTokens.ShouldAllBe(token => token.RevokedAt != null);
        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.SessionRevoked, Ct));
        audit.SessionId.ShouldBe(phone.SessionId);
        audit.UserId.ShouldBe(session.UserId);

        // The phone cannot refresh any more; the laptop's session is untouched and the phone is gone from the list.
        using (var refresh = await PostRefreshAsync(phone.RefreshToken))
        {
            refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await refresh.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("auth.invalid_refresh_token");
        }

        using (var other = await PostRefreshAsync(laptop.RefreshToken))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var list = await GetSessionsAsync(laptop.AccessToken))
        {
            (await list.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid()).ShouldBe([laptop.SessionId]);
        }

        // Revoking it again is not an error (like logging out twice), and writes nothing more.
        using (var again = await DeleteSessionAsync(laptop.AccessToken, phone.SessionId))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await QueryAsync(context => context.Set<AuthAuditLog>().CountAsync(entry => entry.EventType == AuthAuditEvents.SessionRevoked, Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Revoking_the_current_session_is_allowed_and_ends_its_refresh_token()
    {
        await CreateUserAsync(AliceEmail);
        var laptop = await LoginAsync(AliceEmail, "Laptop", "203.0.113.10");

        using (var revoke = await DeleteSessionAsync(laptop.AccessToken, laptop.SessionId))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var refresh = await PostRefreshAsync(laptop.RefreshToken);
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var session = await QueryAsync(context => context.Set<UserSession>().SingleAsync(candidate => candidate.Id == laptop.SessionId, Ct));
        session.RevokedReason.ShouldBe(SessionRevokedReason.Logout);
    }

    [Fact]
    public async Task Revoking_another_users_session_returns_404()
    {
        await CreateUserAsync(AliceEmail);
        await CreateUserAsync(BobEmail);
        var alice = await LoginAsync(AliceEmail, "Laptop", "203.0.113.10");
        var bob = await LoginAsync(BobEmail, "Bob's laptop", "203.0.113.12");
        var unknownId = Guid.NewGuid();

        using var other = await DeleteSessionAsync(alice.AccessToken, bob.SessionId);
        using var unknown = await DeleteSessionAsync(alice.AccessToken, unknownId);

        // Never 403, and the same answer as for an id that does not exist, so ids cannot be probed.
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var otherBody = await other.Content.ReadAsStringAsync(Ct);
        var unknownBody = await unknown.Content.ReadAsStringAsync(Ct);
        var otherJson = JsonDocument.Parse(otherBody).RootElement;
        otherJson.GetProperty("code").GetString().ShouldBe("auth.session_not_found");
        otherJson.GetProperty("params").GetProperty("id").GetGuid().ShouldBe(bob.SessionId);
        Normalize(otherBody, bob.SessionId).ShouldBe(Normalize(unknownBody, unknownId));

        // Bob's session was not touched.
        using var bobRefresh = await PostRefreshAsync(bob.RefreshToken);
        bobRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await QueryAsync(context => context.Set<AuthAuditLog>().CountAsync(entry => entry.EventType == AuthAuditEvents.SessionRevoked, Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Sessions_require_authentication()
    {
        using var list = await Client.GetAsync(SessionsRoute, Ct);
        using var revoke = await Client.DeleteAsync($"{SessionsRoute}/{Guid.NewGuid()}", Ct);

        list.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        revoke.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The trace id and the session id are the only parts that differ between two otherwise identical problem bodies.
    private static string Normalize(string body, Guid id)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        node.Remove("traceId");
        return node.ToJsonString().Replace(id.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Tokens> LoginAsync(string email, string deviceName, string clientAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginRoute)
        {
            Content = JsonContent.Create(new { email, password = AuthTestHarness.Password, deviceName }),
        };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, clientAddress);
        using var response = await Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!, body.GetProperty("sessionId").GetGuid());
    }

    private async Task<HttpResponseMessage> PostRefreshAsync(string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshRoute) { Content = JsonContent.Create(new { refreshToken }) };
        return await Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> GetSessionsAsync(string accessToken, string? query = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, query is null ? SessionsRoute : $"{SessionsRoute}?{query}").WithBearer(accessToken);
        return await Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> DeleteSessionAsync(string accessToken, Guid sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{SessionsRoute}/{sessionId}").WithBearer(accessToken);
        return await Client.SendAsync(request, Ct);
    }

    private async Task ExecuteAsync(Func<AuthDbContext, Task<int>> command)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        (await command(scope.ServiceProvider.GetRequiredService<AuthDbContext>())).ShouldBe(1);
    }

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);
}
