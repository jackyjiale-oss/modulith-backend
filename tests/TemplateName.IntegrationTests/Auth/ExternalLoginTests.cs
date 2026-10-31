using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.UnitTests.Auth.External;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// Social login through the running host against the factory's fake OpenID Connect provider (<c>Google</c> enabled, <c>Apple</c>
/// disabled): sign-up and sign-in, the route's case-insensitive provider, the MFA challenge with first factor <c>fed</c>, a race of two
/// first sign-ins, the provider list and a disabled provider. Every request comes from its own client address and carries its own nonce
/// (the replay guard outlives a test), so neither the strict limit nor a replay decides an outcome by accident.
/// </summary>
public sealed class ExternalLoginTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string ProvidersRoute = "/api/v1/auth/external/providers";
    private const string VerifyRoute = "/api/v1/auth/mfa/verify";
    private const string MeRoute = "/api/v1/auth/me";
    private const string PasswordLoginRoute = "/api/v1/auth/login";
    private const string Email = FakeIdentityProvider.DefaultEmail;

    private int _addressCount;

    [Fact]
    public async Task First_sign_in_with_a_mixed_case_provider_creates_the_account_and_its_tokens_reach_me()
    {
        using var response = await LoginAsync("Google", displayName: "Alice From The Body");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeFalse();
        var accessToken = new JsonWebToken(body.GetProperty("accessToken").GetString());
        Amr(accessToken).ShouldBe(["fed"]);

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, MeRoute).WithBearer(accessToken.EncodedToken);
        using var me = await Client.SendAsync(meRequest, Ct);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var profile = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
        profile.GetProperty("email").GetString().ShouldBe(Email);
        profile.GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
        profile.GetProperty("displayName").GetString().ShouldBe("Alice From The Body");

        var user = await QueryAsync(context => context.Set<User>().AsNoTracking().Include(candidate => candidate.ExternalLogins).SingleAsync(Ct));
        user.HasPassword.ShouldBeFalse();
        var login = user.ExternalLogins.ShouldHaveSingleItem();
        login.Provider.ShouldBe("google");
        login.Subject.ShouldBe(FakeIdentityProvider.DefaultSubject);
        (await AuditEventsAsync()).ShouldBe([AuthAuditEvents.ExternalRegistered, AuthAuditEvents.ExternalLoginSucceeded]);

        // The second sign-in finds the linked identity: the same account, no second one.
        using var again = await LoginAsync("google");
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await QueryAsync(context => context.Set<User>().CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Account_with_mfa_is_not_linked_automatically_and_once_linked_by_hand_gets_a_fed_challenge()
    {
        // A confirmed password account with the provider's email and two-step verification: an automatic link would attach the
        // identity before the second factor is answered, so it is refused (Ruling R14), and nothing is saved but the audit row.
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);

        using var refused = await LoginAsync("google");

        await AssertProblemAsync(refused, HttpStatusCode.Conflict, "auth.external_link_required");
        (await QueryAsync(context => context.Set<UserExternalLogin>().CountAsync(Ct))).ShouldBe(0);
        (await QueryAsync(context => context.Set<MfaChallenge>().CountAsync(Ct))).ShouldBe(0);
        var failure = await QueryAsync(context => context.Set<AuthAuditLog>().AsNoTracking()
            .SingleAsync(entry => entry.EventType == AuthAuditEvents.ExternalLoginFailed, Ct));
        failure.FailureReason.ShouldBe("auth.external_link_required");
        failure.UserId.ShouldBe(user.Id);

        // The user signs in with the password and a code, then links the provider from that code-proven session.
        using var passwordLogin = await PostAsync(PasswordLoginRoute, new { email = Email, password = AuthTestHarness.Password });
        var passwordChallenge = (await passwordLogin.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("challengeToken").GetString();
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        using var codeProven = await PostAsync(VerifyRoute, new { challengeToken = passwordChallenge, code = enrollment.CodeAt(Factory.Time.GetUtcNow()) });
        codeProven.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessionToken = (await codeProven.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString().ShouldNotBeNull();

        using var link = await LinkAsync(sessionToken);
        link.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A later federated sign-in finds the linked identity and asks for the second factor.
        using var login = await LoginAsync("google");

        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var challengeBody = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        challengeBody.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        var challengeToken = challengeBody.GetProperty("challengeToken").GetString().ShouldNotBeNull();
        (await QueryAsync(context => context.Set<MfaChallenge>().AsNoTracking().CountAsync(challenge => challenge.FirstFactor == "fed", Ct))).ShouldBe(1);

        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        using var verify = await PostAsync(VerifyRoute, new { challengeToken, code = enrollment.CodeAt(Factory.Time.GetUtcNow()) });

        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await verify.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Amr(new JsonWebToken(tokens.GetProperty("accessToken").GetString())).ShouldBe(["fed", "otp", "mfa"]);
    }

    [Fact]
    public async Task A_google_address_that_is_neither_gmail_nor_workspace_cannot_link_by_email_but_signs_in_once_linked_by_hand()
    {
        // Ruling R15: without hd, Google's email_verified for a non-Gmail address may be stale, so it neither links nor signs up.
        await CreateUserAsync(Email);
        static void NotWorkspace(IDictionary<string, object> claims) => claims.Remove("hd");

        using var refused = await LoginAsync("google", customize: NotWorkspace);

        await AssertProblemAsync(refused, HttpStatusCode.Forbidden, "auth.external_email_unverified");
        (await QueryAsync(context => context.Set<UserExternalLogin>().CountAsync(Ct))).ShouldBe(0);
        (await QueryAsync(context => context.Set<User>().CountAsync(Ct))).ShouldBe(1);

        // Linked from settings (the link route needs no usable email), the same identity signs in.
        using var passwordLogin = await PostAsync(PasswordLoginRoute, new { email = Email, password = AuthTestHarness.Password });
        var sessionToken = (await passwordLogin.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString().ShouldNotBeNull();
        using var link = await LinkAsync(sessionToken, NotWorkspace);
        link.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var signedIn = await LoginAsync("google", customize: NotWorkspace);

        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await signedIn.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mfaRequired").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Two_simultaneous_first_sign_ins_create_one_account()
    {
        var responses = await Task.WhenAll(LoginAsync("google"), LoginAsync("google"));

        try
        {
            // The loser of the race re-reads and signs in the winner's account; a lost session save is the generic 401, never a 500.
            responses.ShouldContain(response => response.StatusCode == HttpStatusCode.OK);
            responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Unauthorized);
            (await QueryAsync(context => context.Set<User>().CountAsync(Ct))).ShouldBe(1);
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
    public async Task Providers_lists_only_the_enabled_ones()
    {
        using var response = await Client.GetAsync(ProvidersRoute, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var providers = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var only = providers.EnumerateArray().ShouldHaveSingleItem();
        only.GetProperty("name").GetString().ShouldBe("google");
        only.GetProperty("displayName").GetString().ShouldBe("Google");
        only.GetProperty("type").GetString().ShouldBe("oidc");
        only.EnumerateObject().Select(property => property.Name).ShouldBe(["name", "displayName", "type"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_disabled_provider_answers_404_exactly_like_an_unknown_one()
    {
        using var disabled = await LoginAsync("apple");
        using var unknown = await LoginAsync("myspace");

        await ProblemResponseComparison.AssertIdenticalAsync(HttpStatusCode.NotFound, "auth.external_provider_not_found", disabled, unknown);
        (await QueryAsync(context => context.Set<User>().CountAsync(Ct))).ShouldBe(0);
    }

    private static List<string> Amr(JsonWebToken token) => [.. token.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value)];

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(code);
    }

    /// <summary>Posts a fresh ID token of the fake provider, bound to a nonce no request used before, to <c>/google/link</c> as the bearer's user.</summary>
    private async Task<HttpResponseMessage> LinkAsync(string accessToken, Action<IDictionary<string, object>>? customize = null)
    {
        var nonce = $"nonce-{Guid.NewGuid():N}";
        var idToken = Factory.IdentityProvider.IdToken(claims =>
        {
            claims["nonce"] = nonce;
            customize?.Invoke(claims);
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/external/google/link") { Content = JsonContent.Create(new { idToken, nonce }) };
        request.WithBearer(accessToken);
        return await Client.SendAsync(request, Ct);
    }

    /// <summary>
    /// Posts a fresh ID token of the fake provider, bound to a nonce no request used before, to <c>/{provider}/login</c>;
    /// <paramref name="customize"/> changes the token's claims.
    /// </summary>
    private Task<HttpResponseMessage> LoginAsync(string provider, string? displayName = null, Action<IDictionary<string, object>>? customize = null)
    {
        var nonce = $"nonce-{Guid.NewGuid():N}";
        var idToken = Factory.IdentityProvider.IdToken(claims =>
        {
            claims["nonce"] = nonce;
            customize?.Invoke(claims);
        });
        return PostAsync($"/api/v1/auth/external/{provider}/login", new { idToken, nonce, displayName });
    }

    /// <summary>Posts <paramref name="body"/> anonymously from an address no request used before.</summary>
    private async Task<HttpResponseMessage> PostAsync(string route, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, $"198.51.100.{Interlocked.Increment(ref _addressCount)}");
        return await Client.SendAsync(request, Ct);
    }

    private Task<List<string>> AuditEventsAsync()
        => QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).Select(entry => entry.EventType).ToListAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }
}
