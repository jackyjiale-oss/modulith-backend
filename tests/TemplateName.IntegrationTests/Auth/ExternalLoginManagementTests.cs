using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.UnitTests.Auth.External;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// Linking, listing and unlinking external logins through the running host, against the factory's fake OpenID Connect provider
/// (<c>Google</c> enabled, <c>Apple</c> disabled): <c>POST /api/v1/auth/external/{provider}/link</c>,
/// <c>GET /api/v1/auth/external/logins</c> and <c>DELETE /api/v1/auth/external/logins/{id}</c>. Every request names its own bearer token and
/// every proof carries its own nonce (the replay guard outlives a test).
/// </summary>
public sealed class ExternalLoginManagementTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string LinkRoute = "/api/v1/auth/external/google/link";
    private const string LoginsRoute = "/api/v1/auth/external/logins";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string Email = "alice@example.com";
    private const string SecondSubject = "fake-subject-0002";

    private int _addressCount;

    [Fact]
    public async Task Link_attaches_the_identity_audits_it_and_ends_no_session()
    {
        var user = await CreateUserAsync(Email);
        var current = await LoginAsync(Email);
        var other = await LoginAsync(Email);
        var stampBefore = (await FindUserAsync(user.Id)).SecurityStamp;

        using var response = await LinkAsync(current.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("provider").GetString().ShouldBe("google");
        body.GetProperty("email").GetString().ShouldBe(FakeIdentityProvider.DefaultEmail);
        body.GetProperty("lastUsedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("linkedAt").GetDateTimeOffset().ShouldBe(Factory.Time.GetUtcNow());
        body.EnumerateObject().Select(property => property.Name).ShouldBe(["id", "provider", "email", "linkedAt", "lastUsedAt"], ignoreOrder: true);

        var stored = await FindUserAsync(user.Id);
        var login = stored.ExternalLogins.ShouldHaveSingleItem();
        login.Id.ShouldBe(body.GetProperty("id").GetGuid());
        login.Provider.ShouldBe("google");
        login.Subject.ShouldBe(FakeIdentityProvider.DefaultSubject);

        // Linking adds a way in: the stamp stays and no session ends.
        stored.SecurityStamp.ShouldBe(stampBefore);
        (await QueryAsync(context => context.Set<UserSession>().CountAsync(session => session.RevokedAt != null, Ct))).ShouldBe(0);
        other.SessionId.ShouldNotBe(current.SessionId);

        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.ExternalLinked, Ct));
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(user.Id);
        audit.SessionId.ShouldBe(current.SessionId);
        audit.Details.ShouldBe("""{"provider":"google"}""");
    }

    [Fact]
    public async Task Linking_the_same_identity_again_is_200_and_changes_nothing()
    {
        await CreateUserAsync(Email);
        var tokens = await LoginAsync(Email);
        using var first = await LinkAsync(tokens.AccessToken);
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        using var again = await LinkAsync(tokens.AccessToken);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid().ShouldBe(id);
        (await QueryAsync(context => context.Set<UserExternalLogin>().CountAsync(Ct))).ShouldBe(1);
        (await QueryAsync(context => context.Set<AuthAuditLog>().CountAsync(entry => entry.EventType == AuthAuditEvents.ExternalLinked, Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Link_without_a_fresh_step_up_is_403_step_up_required()
    {
        await CreateUserAsync(Email);
        var tokens = await LoginAsync(Email);
        Factory.Time.Advance(TimeSpan.FromMinutes(11));

        // A refresh renews the access token but not the proof: auth_time stays the sign-in's.
        var refreshed = await RefreshAsync(tokens.RefreshToken);
        using var response = await LinkAsync(refreshed);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "auth.step_up_required");
        (await QueryAsync(context => context.Set<UserExternalLogin>().CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task An_account_with_two_step_verification_needs_a_session_that_proved_a_code()
    {
        var user = await CreateUserAsync(Email);
        await EnrollTotpAsync(user.Id);
        var tokens = await StartPasswordSessionAsync(user);

        // The password's fresh step-up is not enough for such an account.
        using var response = await LinkAsync(tokens.AccessToken);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "auth.step_up_required");
        (await QueryAsync(context => context.Set<UserExternalLogin>().CountAsync(Ct))).ShouldBe(0);
    }

    [Theory]
    [InlineData("apple")]
    [InlineData("myspace")]
    public async Task A_disabled_or_unknown_provider_cannot_be_linked(string provider)
    {
        await CreateUserAsync(Email);
        var tokens = await LoginAsync(Email);

        using var response = await LinkAsync(tokens.AccessToken, provider);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "auth.external_provider_not_found");
    }

    [Fact]
    public async Task An_identity_that_belongs_to_another_user_is_409_identity_taken()
    {
        using var signUp = await ExternalSignUpAsync();
        signUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = await CreateUserAsync("bob@example.com");
        var tokens = await LoginAsync(other.Email);

        using var response = await LinkAsync(tokens.AccessToken);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "auth.external_identity_taken");
        (await QueryAsync(context => context.Set<UserExternalLogin>().CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task A_second_identity_for_one_provider_is_409_provider_already_linked()
    {
        await CreateUserAsync(Email);
        var tokens = await LoginAsync(Email);
        using var first = await LinkAsync(tokens.AccessToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var second = await LinkAsync(tokens.AccessToken, subject: SecondSubject);

        await AssertProblemAsync(second, HttpStatusCode.Conflict, "auth.external_provider_already_linked");
        (await QueryAsync(context => context.Set<UserExternalLogin>().SingleAsync(Ct))).Subject.ShouldBe(FakeIdentityProvider.DefaultSubject);
    }

    [Fact]
    public async Task Two_users_linking_one_identity_at_once_leave_one_owner_and_no_500()
    {
        var alice = await CreateUserAsync(Email);
        var bob = await CreateUserAsync("bob@example.com");
        var aliceTokens = await LoginAsync(alice.Email);
        var bobTokens = await LoginAsync(bob.Email);

        var responses = await Task.WhenAll(LinkAsync(aliceTokens.AccessToken), LinkAsync(bobTokens.AccessToken));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
            (await QueryAsync(context => context.Set<UserExternalLogin>().CountAsync(Ct))).ShouldBe(1);
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
    public async Task List_shows_has_password_and_every_login_including_a_disabled_providers_by_its_key()
    {
        var user = await CreateUserAsync(Email);
        var tokens = await LoginAsync(Email);
        using var linked = await LinkAsync(tokens.AccessToken);
        linked.StatusCode.ShouldBe(HttpStatusCode.OK);
        await AddLoginAsync(user.Id, "apple", "apple-subject-0001", "alice@icloud.example");

        using var response = await SendAsync(HttpMethod.Get, LoginsRoute, tokens.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(Ct);

        // The identity at the provider (its subject) never leaves the server.
        text.ShouldNotContain(FakeIdentityProvider.DefaultSubject);
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        body.GetProperty("hasPassword").GetBoolean().ShouldBeTrue();
        var logins = body.GetProperty("logins").EnumerateArray().ToList();
        logins.Select(login => login.GetProperty("provider").GetString()).ShouldBe(["apple", "google"]);
        logins[0].GetProperty("email").GetString().ShouldBe("alice@icloud.example");
        logins[1].GetProperty("email").GetString().ShouldBe(FakeIdentityProvider.DefaultEmail);
        logins.ShouldAllBe(login => login.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(
            new[] { "email", "id", "lastUsedAt", "linkedAt", "provider" }));
    }

    [Fact]
    public async Task List_of_a_user_without_a_password_says_so_and_shows_only_the_callers_logins()
    {
        using var signUp = await ExternalSignUpAsync();
        var tokens = await TokensOfAsync(signUp);
        await CreateUserAsync("bob@example.com");
        var bob = await LoginAsync("bob@example.com");
        using var bobLink = await LinkAsync(bob.AccessToken, subject: SecondSubject);
        bobLink.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await SendAsync(HttpMethod.Get, LoginsRoute, tokens.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("hasPassword").GetBoolean().ShouldBeFalse();
        var login = body.GetProperty("logins").EnumerateArray().ShouldHaveSingleItem();
        login.GetProperty("provider").GetString().ShouldBe("google");
        login.GetProperty("lastUsedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_of_a_suspended_caller_is_the_bare_401_of_a_request_without_a_valid_token()
    {
        var user = await CreateUserAsync(Email);
        var tokens = await LoginAsync(Email);
        await SuspendAsync(user.Id);

        using var response = await SendAsync(HttpMethod.Get, LoginsRoute, tokens.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldNotBe("application/problem+json");
    }

    [Fact]
    public async Task Unlink_removes_the_login_rotates_the_stamp_and_ends_only_the_other_sessions()
    {
        var user = await CreateUserAsync(Email);
        var current = await LoginAsync(Email);
        var other = await LoginAsync(Email);
        using var linked = await LinkAsync(current.AccessToken);
        var id = (await linked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var stampBefore = (await FindUserAsync(user.Id)).SecurityStamp;

        using var response = await SendAsync(HttpMethod.Delete, $"{LoginsRoute}/{id}", current.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var stored = await FindUserAsync(user.Id);
        stored.ExternalLogins.ShouldBeEmpty();
        stored.SecurityStamp.ShouldNotBe(stampBefore);

        var sessions = await QueryAsync(context => context.Set<UserSession>().AsNoTracking().ToListAsync(Ct));
        var keptSession = sessions.Single(session => session.Id == current.SessionId);
        keptSession.RevokedAt.ShouldBeNull();
        keptSession.SecurityStamp.ShouldBe(stored.SecurityStamp);
        var endedSession = sessions.Single(session => session.Id == other.SessionId);
        endedSession.RevokedReason.ShouldBe(SessionRevokedReason.ExternalLoginChanged);

        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.ExternalUnlinked, Ct));
        audit.UserId.ShouldBe(user.Id);
        audit.SessionId.ShouldBe(current.SessionId);
        audit.Details.ShouldBe("""{"provider":"google","revokedSessionCount":1}""");

        // The other session's refresh token is dead; the current one still refreshes.
        using var endedRefresh = await Client.PostAsJsonAsync(RefreshRoute, new { refreshToken = other.RefreshToken }, Ct);
        endedRefresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var keptRefresh = await Client.PostAsJsonAsync(RefreshRoute, new { refreshToken = current.RefreshToken }, Ct);
        keptRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unlink_works_for_a_login_of_a_switched_off_provider()
    {
        var user = await CreateUserAsync(Email);
        var tokens = await LoginAsync(Email);
        var id = await AddLoginAsync(user.Id, "apple", "apple-subject-0001", null);

        using var response = await SendAsync(HttpMethod.Delete, $"{LoginsRoute}/{id}", tokens.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await QueryAsync(context => context.Set<UserExternalLogin>().CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task The_only_way_to_sign_in_cannot_be_unlinked()
    {
        using var signUp = await ExternalSignUpAsync();
        var tokens = await TokensOfAsync(signUp);
        var user = await QueryAsync(context => context.Set<User>().AsNoTracking().Include(candidate => candidate.ExternalLogins).SingleAsync(Ct));
        var login = user.ExternalLogins.ShouldHaveSingleItem();

        using var response = await SendAsync(HttpMethod.Delete, $"{LoginsRoute}/{login.Id}", tokens.AccessToken);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "auth.last_login_method");
        var after = await QueryAsync(context => context.Set<User>().AsNoTracking().Include(candidate => candidate.ExternalLogins).SingleAsync(Ct));
        after.ExternalLogins.ShouldHaveSingleItem();
        after.SecurityStamp.ShouldBe(user.SecurityStamp);
    }

    [Fact]
    public async Task An_unknown_login_and_another_users_login_are_alike_404()
    {
        var alice = await CreateUserAsync(Email);
        var bob = await CreateUserAsync("bob@example.com");
        var aliceLogin = await AddLoginAsync(alice.Id, "google", FakeIdentityProvider.DefaultSubject, null);
        var bobTokens = await LoginAsync(bob.Email);

        using var unknown = await SendAsync(HttpMethod.Delete, $"{LoginsRoute}/{Guid.NewGuid()}", bobTokens.AccessToken);
        using var foreign = await SendAsync(HttpMethod.Delete, $"{LoginsRoute}/{aliceLogin}", bobTokens.AccessToken);

        await ProblemResponseComparison.AssertIdenticalAsync(HttpStatusCode.NotFound, "auth.external_login_not_found", unknown, foreign);
        (await QueryAsync(context => context.Set<UserExternalLogin>().CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Unlink_without_a_fresh_step_up_is_403_step_up_required()
    {
        var user = await CreateUserAsync(Email);
        var tokens = await LoginAsync(Email);
        var id = await AddLoginAsync(user.Id, "google", FakeIdentityProvider.DefaultSubject, null);
        Factory.Time.Advance(TimeSpan.FromMinutes(11));
        var refreshed = await RefreshAsync(tokens.RefreshToken);

        using var response = await SendAsync(HttpMethod.Delete, $"{LoginsRoute}/{id}", refreshed);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "auth.step_up_required");
        (await QueryAsync(context => context.Set<UserExternalLogin>().CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Every_route_needs_a_signed_in_caller()
    {
        using var link = await Client.PostAsJsonAsync(LinkRoute, new { idToken = "x", nonce = "nonce-0123456789abcdef" }, Ct);
        using var list = await Client.GetAsync(LoginsRoute, Ct);
        using var unlink = await Client.DeleteAsync($"{LoginsRoute}/{Guid.NewGuid()}", Ct);

        link.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        list.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unlink.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(code);
    }

    /// <summary>Posts a fresh ID token of the fake provider, bound to a nonce no request used before, to <c>/{provider}/link</c>.</summary>
    private Task<HttpResponseMessage> LinkAsync(string accessToken, string provider = "google", string subject = FakeIdentityProvider.DefaultSubject)
    {
        var nonce = $"nonce-{Guid.NewGuid():N}";
        var idToken = Factory.IdentityProvider.IdToken(claims =>
        {
            claims["nonce"] = nonce;
            claims["sub"] = subject;
        });
        return SendAsync(HttpMethod.Post, $"/api/v1/auth/external/{provider}/link", accessToken, new { idToken, nonce });
    }

    /// <summary>Signs a new user up with the fake provider's default identity (no password, email confirmed).</summary>
    private async Task<HttpResponseMessage> ExternalSignUpAsync()
    {
        var nonce = $"nonce-{Guid.NewGuid():N}";
        var idToken = Factory.IdentityProvider.IdToken(claims => claims["nonce"] = nonce);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/external/google/login") { Content = JsonContent.Create(new { idToken, nonce }) };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, NextAddress());
        return await Client.SendAsync(request, Ct);
    }

    private async Task<Tokens> LoginAsync(string email)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginRoute) { Content = JsonContent.Create(new { email, password = AuthTestHarness.Password }) };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, NextAddress());
        using var response = await Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await TokensOfAsync(response);
    }

    /// <summary>
    /// A session of <paramref name="user"/> that carries only the password and a fresh <c>auth_time</c>. The password login of an account with
    /// two-step verification gives a challenge, not a session, so the session is written straight to the database, as a session whose
    /// second factor was never proved would be.
    /// </summary>
    private async Task<Tokens> StartPasswordSessionAsync(User user)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var now = Factory.Time.GetUtcNow();
        var (session, _) = UserSession.Start(
            user.Id,
            AuthenticationMethods.Password,
            "Integration tests",
            userAgent: null,
            ipAddress: null,
            user.SecurityStamp,
            [.. Enumerable.Range(1, 32).Select(value => (byte)value)],
            TimeSpan.FromDays(14),
            TimeSpan.FromDays(90),
            now);
        context.Add(session);
        await context.SaveChangesAsync(Ct);

        var accessToken = scope.ServiceProvider
            .GetRequiredService<IAccessTokenIssuer>()
            .Issue(new AccessTokenRequest(user.Id, session.Id, session.SecurityStamp, session.AuthMethods, now, user.Locale))
            .Value;
        return new Tokens(accessToken, string.Empty, session.Id);
    }

    private static async Task<Tokens> TokensOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!, body.GetProperty("sessionId").GetGuid());
    }

    private async Task<string> RefreshAsync(string refreshToken)
    {
        using var response = await Client.PostAsJsonAsync(RefreshRoute, new { refreshToken }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, string accessToken, object? body = null)
    {
        using var request = new HttpRequestMessage(method, route).WithBearer(accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await Client.SendAsync(request, Ct);
    }

    private string NextAddress() => $"198.51.100.{Interlocked.Increment(ref _addressCount)}";

    private Task<User> FindUserAsync(Guid userId)
        => QueryAsync(context => context.Set<User>().AsNoTracking().Include(user => user.ExternalLogins).SingleAsync(user => user.Id == userId, Ct));

    /// <summary>Adds a login straight to the database, as one linked while its provider was still switched on.</summary>
    private async Task<Guid> AddLoginAsync(Guid userId, string provider, string subject, string? email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await context.Set<User>().Include(candidate => candidate.ExternalLogins).SingleAsync(candidate => candidate.Id == userId, Ct);
        var login = user.LinkExternalLogin(provider, subject, email, Factory.Time.GetUtcNow()).Value;
        await context.SaveChangesAsync(Ct);
        return login.Id;
    }

    private async Task SuspendAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await context.Set<User>().SingleAsync(candidate => candidate.Id == userId, Ct);
        user.Suspend(Factory.Time.GetUtcNow());
        await context.SaveChangesAsync(Ct);
    }

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);
}
