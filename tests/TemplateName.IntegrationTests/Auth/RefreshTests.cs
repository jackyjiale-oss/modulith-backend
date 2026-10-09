using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Sessions.Events;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// <c>POST /api/v1/auth/token/refresh</c>, <c>POST /api/v1/auth/logout</c> and <c>POST /api/v1/auth/logout-all</c> through the running
/// host: rotation, reuse detection (also between two simultaneous requests, Review Focus 2), the sliding and absolute lifetimes on
/// <see cref="IntegrationTestWebAppFactory.Time"/>, the security stamp, and what logging out does and does not end (decision D9).
/// </summary>
public sealed class RefreshTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string LoginRoute = "/api/v1/auth/login";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string LogoutRoute = "/api/v1/auth/logout";
    private const string LogoutAllRoute = "/api/v1/auth/logout-all";
    private const string MeRoute = "/api/v1/auth/me";
    private const string Email = "alice@example.com";
    private const string ClientAddress = "203.0.113.21";

    private static readonly TimeSpan Sliding = TimeSpan.FromDays(14);
    private static readonly TimeSpan Absolute = TimeSpan.FromDays(90);

    [Fact]
    public async Task Refresh_rotates_and_the_old_token_stops_working()
    {
        var user = await CreateUserAsync(Email, locale: "ms");
        var signedInAt = Factory.Time.GetUtcNow();
        var login = await LoginAsync();
        Factory.Time.Advance(TimeSpan.FromMinutes(20));
        var now = Factory.Time.GetUtcNow();

        using var response = await RefreshAsync(login.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refreshed = await ReadTokensAsync(response);
        refreshed.SessionId.ShouldBe(login.SessionId);
        refreshed.RefreshToken.ShouldNotBe(login.RefreshToken);
        refreshed.RefreshToken.Length.ShouldBe(43);
        refreshed.AccessToken.ShouldNotBe(login.AccessToken);
        refreshed.AccessTokenExpiresAt.ShouldBe(now.AddMinutes(10));
        refreshed.RefreshTokenExpiresAt.ShouldBe(now + Sliding);

        // The new access token belongs to the same session and keeps the sign-in's amr and auth_time.
        var token = new JsonWebToken(refreshed.AccessToken);
        token.GetClaim("sub").Value.ShouldBe(user.Id.ToString());
        token.GetClaim("sid").Value.ShouldBe(login.SessionId.ToString());
        token.GetClaim("sst").Value.ShouldBe(user.SecurityStamp);
        token.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value).ShouldBe(["pwd"]);
        token.GetClaim("auth_time").Value.ShouldBe(signedInAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        token.GetClaim("locale").Value.ShouldBe("ms");
        using (var me = await GetMeAsync(refreshed.AccessToken))
        {
            me.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Stored: the old token used and replaced by the new one, whose hash only is kept.
        var session = await LoadSessionAsync(login.SessionId);
        session.LastSeenAt.ShouldBe(now);
        session.RevokedAt.ShouldBeNull();
        var old = session.RefreshTokens.Single(candidate => candidate.TokenHash.SequenceEqual(Hash(login.RefreshToken)));
        var replacement = session.RefreshTokens.Single(candidate => candidate.TokenHash.SequenceEqual(Hash(refreshed.RefreshToken)));
        old.UsedAt.ShouldBe(now);
        old.ReplacedByTokenId.ShouldBe(replacement.Id);
        replacement.UsedAt.ShouldBeNull();
        (await AuditEventsAsync()).ShouldContain(AuthAuditEvents.TokenRefreshed);

        using var again = await RefreshAsync(login.RefreshToken);
        await AssertProblemAsync(again, "auth.refresh_token_reused");
    }

    [Fact]
    public async Task Reusing_a_rotated_token_revokes_the_whole_session()
    {
        var user = await CreateUserAsync(Email);
        var login = await LoginAsync();
        using var first = await RefreshAsync(login.RefreshToken);
        var newer = await ReadTokensAsync(first);

        using var reuse = await RefreshAsync(login.RefreshToken);

        await AssertProblemAsync(reuse, "auth.refresh_token_reused");

        // The newer token, which the thief or the owner may hold, fails too: the session is revoked.
        using var newerAttempt = await RefreshAsync(newer.RefreshToken);
        await AssertProblemAsync(newerAttempt, "auth.invalid_refresh_token");

        var session = await LoadSessionAsync(login.SessionId);
        session.RevokedReason.ShouldBe(SessionRevokedReason.TokenReuse);
        session.RefreshTokens.ShouldAllBe(token => token.RevokedAt != null);
        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.TokenReuseDetected, Ct));
        audit.UserId.ShouldBe(user.Id);
        audit.SessionId.ShouldBe(login.SessionId);
        audit.Succeeded.ShouldBeFalse();
        audit.IpAddress.ShouldBe(ClientAddress);
        (await OutboxTypesAsync()).Count(type => type.Contains(nameof(RefreshTokenReuseDetectedDomainEvent), StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_refresh_with_same_token_succeeds_once()
    {
        await CreateUserAsync(Email);
        var login = await LoginAsync();

        var responses = await Task.WhenAll(RefreshAsync(login.RefreshToken), RefreshAsync(login.RefreshToken));

        try
        {
            responses.Select(response => (int)response.StatusCode).Order().ShouldBe([200, 401]);
            var winner = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
            var loser = responses.Single(response => response.StatusCode == HttpStatusCode.Unauthorized);
            await AssertProblemAsync(loser, "auth.refresh_token_reused");
            var winnerTokens = await ReadTokensAsync(winner);

            // The loser's claim failed, which is reuse: the session is revoked, so the winner's new refresh token is dead too.
            var session = await LoadSessionAsync(login.SessionId);
            session.RevokedAt.ShouldNotBeNull();
            session.RevokedReason.ShouldBe(SessionRevokedReason.TokenReuse);
            using var afterwards = await RefreshAsync(winnerTokens.RefreshToken);
            await AssertProblemAsync(afterwards, "auth.invalid_refresh_token");
            (await AuditEventsAsync()).Count(type => type == AuthAuditEvents.TokenReuseDetected).ShouldBe(1);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Refresh_after_logout_fails()
    {
        await CreateUserAsync(Email);
        var login = await LoginAsync();

        using var logout = await PostAuthenticatedAsync(LogoutRoute, login.AccessToken);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var refresh = await RefreshAsync(login.RefreshToken);

        await AssertProblemAsync(refresh, "auth.invalid_refresh_token");
        var session = await LoadSessionAsync(login.SessionId);
        session.RevokedReason.ShouldBe(SessionRevokedReason.Logout);
        (await AuditEventsAsync()).ShouldNotContain(AuthAuditEvents.TokenReuseDetected);

        // Logging out again is still a success.
        using var again = await PostAuthenticatedAsync(LogoutRoute, login.AccessToken);
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AuditEventsAsync()).Count(type => type == AuthAuditEvents.Logout).ShouldBe(1);
    }

    [Fact]
    public async Task Logout_all_invalidates_every_session_refresh_token()
    {
        await CreateUserAsync(Email);
        var laptop = await LoginAsync();
        var phone = await LoginAsync();
        using (var rotated = await RefreshAsync(phone.RefreshToken))
        {
            phone = await ReadTokensAsync(rotated);
        }

        using var logoutAll = await PostAuthenticatedAsync(LogoutAllRoute, laptop.AccessToken);

        logoutAll.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        foreach (var tokens in new[] { laptop, phone })
        {
            using var refresh = await RefreshAsync(tokens.RefreshToken);
            await AssertProblemAsync(refresh, "auth.invalid_refresh_token");
            (await LoadSessionAsync(tokens.SessionId)).RevokedReason.ShouldBe(SessionRevokedReason.LogoutAll);
        }

        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.LogoutAll, Ct));
        audit.SessionId.ShouldBe(laptop.SessionId);
        audit.Details.ShouldBe("""{"sessionCount":2}""");
    }

    [Fact]
    public async Task Logout_all_leaves_other_users_alone()
    {
        await CreateUserAsync(Email);
        await CreateUserAsync("bob@example.com");
        var alice = await LoginAsync();
        var bob = await LoginAsync("bob@example.com");

        using var logoutAll = await PostAuthenticatedAsync(LogoutAllRoute, alice.AccessToken);

        logoutAll.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var refresh = await RefreshAsync(bob.RefreshToken);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_does_not_touch_other_sessions()
    {
        await CreateUserAsync(Email);
        var laptop = await LoginAsync();
        var phone = await LoginAsync();

        using var logout = await PostAuthenticatedAsync(LogoutRoute, laptop.AccessToken);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var phoneRefresh = await RefreshAsync(phone.RefreshToken);
        phoneRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var laptopRefresh = await RefreshAsync(laptop.RefreshToken);
        await AssertProblemAsync(laptopRefresh, "auth.invalid_refresh_token");
        (await LoadSessionAsync(phone.SessionId)).RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Logout_and_logout_all_need_a_signed_in_user()
    {
        foreach (var route in new[] { LogoutRoute, LogoutAllRoute })
        {
            using var response = await Client.PostAsync(route, content: null, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Refresh_after_expiry_fails_as_expired()
    {
        await CreateUserAsync(Email);
        var login = await LoginAsync();

        // One millisecond (the column precision) before the sliding expiry the token still works ...
        Factory.Time.Advance(Sliding - TimeSpan.FromMilliseconds(1));
        using (var justInTime = await RefreshAsync(login.RefreshToken))
        {
            justInTime.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // ... at the expiry it does not, and an expired token tried twice is not taken for reuse.
        var second = await LoginAsync();
        Factory.Time.Advance(Sliding);
        using var expired = await RefreshAsync(second.RefreshToken);
        await AssertProblemAsync(expired, "auth.refresh_token_expired");
        using var expiredAgain = await RefreshAsync(second.RefreshToken);
        await AssertProblemAsync(expiredAgain, "auth.refresh_token_expired");
        (await LoadSessionAsync(second.SessionId)).RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Sliding_window_moves_forward_but_never_beyond_the_absolute_lifetime()
    {
        await CreateUserAsync(Email);
        var signedInAt = Factory.Time.GetUtcNow();
        var tokens = await LoginAsync();
        var sessionEnd = signedInAt + Absolute;

        // Refresh every 13 days: each new token gets a fresh 14-day window until the session's absolute end caps it.
        while (Factory.Time.GetUtcNow() + TimeSpan.FromDays(13) < sessionEnd)
        {
            Factory.Time.Advance(TimeSpan.FromDays(13));
            var now = Factory.Time.GetUtcNow();
            using var response = await RefreshAsync(tokens.RefreshToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            tokens = await ReadTokensAsync(response);
            tokens.RefreshTokenExpiresAt.ShouldBe(now + Sliding < sessionEnd ? now + Sliding : sessionEnd);
        }

        tokens.RefreshTokenExpiresAt.ShouldBe(sessionEnd);
        Factory.Time.Advance(sessionEnd - Factory.Time.GetUtcNow());
        using var atTheEnd = await RefreshAsync(tokens.RefreshToken);
        await AssertProblemAsync(atTheEnd, "auth.refresh_token_expired");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("%00'; DROP TABLE auth.RefreshTokens; --")]
    [InlineData("é中😀")]
    public async Task Refresh_with_garbage_returns_401_not_500(string token)
    {
        await CreateUserAsync(Email);
        var login = await LoginAsync();
        using (var logout = await PostAuthenticatedAsync(LogoutRoute, login.AccessToken))
        {
            logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var garbage = await RefreshAsync(token);
        using var revoked = await RefreshAsync(login.RefreshToken);

        // A token that never existed and one whose session was ended answer exactly alike.
        var garbageBody = await AssertProblemAsync(garbage, "auth.invalid_refresh_token");
        var revokedBody = await AssertProblemAsync(revoked, "auth.invalid_refresh_token");
        foreach (var property in garbageBody.EnumerateObject().Where(property => property.Name != "traceId"))
        {
            revokedBody.GetProperty(property.Name).GetRawText().ShouldBe(property.Value.GetRawText(), property.Name);
        }

        revokedBody.EnumerateObject().Count().ShouldBe(garbageBody.EnumerateObject().Count());
    }

    [Fact]
    public async Task Refresh_with_an_empty_or_malformed_body_is_a_400()
    {
        using var empty = await RefreshAsync(string.Empty);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var tooLong = await RefreshAsync(new string('a', 257));
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshRoute) { Content = new StringContent("{ not json", Encoding.UTF8, "application/json") };
        using var malformed = await Client.SendAsync(request, Ct);
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refresh_after_security_stamp_change_fails()
    {
        var user = await CreateUserAsync(Email);
        var login = await LoginAsync();
        await ChangePasswordInDatabaseAsync(user.Id);

        using var refresh = await RefreshAsync(login.RefreshToken);

        await AssertProblemAsync(refresh, "auth.invalid_refresh_token");
        var session = await LoadSessionAsync(login.SessionId);
        session.RevokedReason.ShouldBe(SessionRevokedReason.PasswordChanged);
        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.RefreshFailed, Ct));
        audit.FailureReason.ShouldBe("security_stamp_changed");
        audit.SessionId.ShouldBe(login.SessionId);
    }

    [Fact]
    public async Task Access_token_of_a_logged_out_session_still_validates_until_it_expires()
    {
        await CreateUserAsync(Email);
        var login = await LoginAsync();

        using (var logout = await PostAuthenticatedAsync(LogoutRoute, login.AccessToken))
        {
            logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Decision D9: access tokens are not checked against the session on every request, so this one works until its exp.
        using (var stillValid = await GetMeAsync(login.AccessToken))
        {
            stillValid.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Past exp plus the 30-second clock skew it is refused, and the refresh token cannot replace it.
        Factory.Time.Advance(login.AccessTokenExpiresAt - Factory.Time.GetUtcNow() + TimeSpan.FromSeconds(31));
        using var expired = await GetMeAsync(login.AccessToken);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var refresh = await RefreshAsync(login.RefreshToken);
        await AssertProblemAsync(refresh, "auth.invalid_refresh_token");
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static async Task<Tokens> ReadTokensAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Tokens(
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("accessTokenExpiresAt").GetDateTimeOffset(),
            body.GetProperty("refreshToken").GetString()!,
            body.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset(),
            body.GetProperty("sessionId").GetGuid());
    }

    /// <summary>Asserts a 401 problem with <paramref name="code"/> and returns its body (the content can be read only once).</summary>
    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, string code)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe(code);
        body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
        return body;
    }

    private async Task<Tokens> LoginAsync(string email = Email)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginRoute)
        {
            Content = JsonContent.Create(new { email, password = AuthTestHarness.Password }),
        };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, ClientAddress);
        using var response = await Client.SendAsync(request, Ct);
        return await ReadTokensAsync(response);
    }

    private async Task<HttpResponseMessage> RefreshAsync(string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshRoute) { Content = JsonContent.Create(new { refreshToken }) };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, ClientAddress);
        return await Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> PostAuthenticatedAsync(string route, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route).WithBearer(accessToken);
        return await Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> GetMeAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MeRoute).WithBearer(accessToken);
        return await Client.SendAsync(request, Ct);
    }

    private async Task ChangePasswordInDatabaseAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await context.Set<User>().SingleAsync(candidate => candidate.Id == userId, Ct);
        user.ChangePassword("changed-elsewhere-hash", historyCount: 5, Factory.Time.GetUtcNow());
        await context.SaveChangesAsync(Ct);
    }

    private Task<UserSession> LoadSessionAsync(Guid sessionId)
        => QueryAsync(context => context.Set<UserSession>().Include(session => session.RefreshTokens).SingleAsync(session => session.Id == sessionId, Ct));

    private Task<List<string>> AuditEventsAsync()
        => QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).Select(entry => entry.EventType).ToListAsync(Ct));

    private Task<List<string>> OutboxTypesAsync()
        => QueryAsync(context => context.Set<OutboxMessage>().Select(message => message.Type).ToListAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt, Guid SessionId);
}
