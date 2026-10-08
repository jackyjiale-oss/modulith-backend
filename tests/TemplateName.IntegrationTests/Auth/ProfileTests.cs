using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Resources;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.Modules.Auth.Resources;

namespace TemplateName.IntegrationTests.Auth;

/// <summary><c>PUT /api/v1/auth/me</c>: the signed-in user changes their display name, language and time zone.</summary>
public sealed class ProfileTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string LoginRoute = "/api/v1/auth/login";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string MeRoute = "/api/v1/auth/me";
    private const string SessionsRoute = "/api/v1/auth/sessions";
    private const string Email = "alice@example.com";

    [Fact]
    public async Task Update_profile_changes_locale_and_localizes_the_next_error_response()
    {
        await CreateUserAsync(Email);
        var tokens = await LoginAsync();

        using (var update = await PutMeAsync(tokens.AccessToken, new { displayName = "Alicia", locale = "ms", timeZone = "Asia/Kuala_Lumpur" }))
        {
            update.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // The language is read from the access token's locale claim, not looked up per request, so the token issued before the change
        // still answers in English ...
        var failure = $"{SessionsRoute}/{Guid.NewGuid()}";
        using (var stale = await DeleteAsync(tokens.AccessToken, failure))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await stale.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString()!
                .ShouldStartWith("Session '");
        }

        // ... and the next token the session issues (a refresh) carries the saved locale, which wins over a missing Accept-Language (D7).
        var refreshed = await RefreshAsync(tokens.RefreshToken);
        using var response = await DeleteAsync(refreshed.AccessToken, failure);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentLanguage.ShouldBe(["ms"]);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("auth.session_not_found");
        body.GetProperty("detail").GetString().ShouldBe(MalayMessage("auth.session_not_found").Replace("{id}", failure.Split('/')[^1], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_profile_returns_the_current_user_and_persists_the_changes()
    {
        var user = await SignInAsync("auth.user.view");

        using var response = await Client.PutAsJsonAsync(MeRoute, new { displayName = "  Alicia Liddell ", locale = "MS-my", timeZone = "Asia/Kuala_Lumpur" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("id").GetGuid().ShouldBe(user.UserId);
        body.GetProperty("displayName").GetString().ShouldBe("Alicia Liddell");
        body.GetProperty("locale").GetString().ShouldBe("ms-MY");
        body.GetProperty("timeZone").GetString().ShouldBe("Asia/Kuala_Lumpur");
        body.GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
        body.GetProperty("roles").GetArrayLength().ShouldBe(1);
        body.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()).ShouldBe(["auth.user.view"]);

        // What GET me reads is what was saved, and the change is audited.
        using var me = await Client.GetAsync(MeRoute, Ct);
        var current = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
        current.GetProperty("displayName").GetString().ShouldBe("Alicia Liddell");
        current.GetProperty("locale").GetString().ShouldBe("ms-MY");
        current.GetProperty("timeZone").GetString().ShouldBe("Asia/Kuala_Lumpur");
        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.ProfileUpdated, Ct));
        audit.UserId.ShouldBe(user.UserId);
        audit.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", "ms", "UTC", "displayName")]
    [InlineData("Alicia", "xx-invalid", "UTC", "locale")]
    [InlineData("Alicia", "ms", "Mars/Olympus_Mons", "timeZone")]
    public async Task Update_profile_with_invalid_values_is_400_and_changes_nothing(string displayName, string locale, string timeZone, string field)
    {
        var user = await SignInAsync();

        using var response = await Client.PutAsJsonAsync(MeRoute, new { displayName, locale, timeZone }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("validation.failed");
        body.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        var stored = await QueryAsync(context => context.Set<User>().SingleAsync(candidate => candidate.Id == user.UserId, Ct));
        stored.DisplayName.ShouldBe("Test user");
        stored.Locale.ShouldBe("en");
        stored.TimeZone.ShouldBe("UTC");
    }

    [Fact]
    public async Task Update_profile_for_a_suspended_user_is_401_like_get_me()
    {
        var user = await SignInAsync();
        await QueryAsync(async context =>
        {
            var stored = await context.Set<User>().SingleAsync(candidate => candidate.Id == user.UserId, Ct);
            stored.Suspend(Factory.Time.GetUtcNow());
            return await context.SaveChangesAsync(Ct);
        });

        using var response = await Client.PutAsJsonAsync(MeRoute, new { displayName = "Alicia", locale = "ms", timeZone = "UTC" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("http.401");
    }

    [Fact]
    public async Task Update_profile_requires_authentication()
    {
        using var response = await Client.PutAsJsonAsync(MeRoute, new { displayName = "Alicia", locale = "ms", timeZone = "UTC" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("http.401");
    }

    private static string MalayMessage(string key)
        => new ResourceManager(typeof(AuthErrorMessages).FullName!, typeof(AuthErrorMessages).Assembly)
            .GetResourceSet(CultureInfo.GetCultureInfo("ms"), createIfNotExists: true, tryParents: false)!
            .GetString(key)!;

    private async Task<Tokens> LoginAsync()
    {
        using var response = await Client.PostAsJsonAsync(LoginRoute, new { email = Email, password = AuthTestHarness.Password }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private async Task<Tokens> RefreshAsync(string refreshToken)
    {
        using var response = await Client.PostAsJsonAsync(RefreshRoute, new { refreshToken }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private async Task<HttpResponseMessage> PutMeAsync(string accessToken, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, MeRoute) { Content = JsonContent.Create(body) }.WithBearer(accessToken);
        return await Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> DeleteAsync(string accessToken, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, route).WithBearer(accessToken);
        return await Client.SendAsync(request, Ct);
    }

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken);
}
