using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TemplateName.Application.Common.Security;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// <c>POST /api/v1/auth/step-up</c> through the running host: a fresh <c>auth_time</c> and <c>amr</c> in a new access token of the same
/// session, what a refresh carries afterwards, replay of a TOTP step, an unreadable secret, the rate limit and the fail-closed 401.
/// </summary>
public sealed class StepUpTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string StepUpRoute = "/api/v1/auth/step-up";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string LogoutRoute = "/api/v1/auth/logout";
    private const string Email = "alice@example.com";

    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task Step_up_with_password_returns_a_token_with_fresh_auth_time_and_pwd()
    {
        var signedIn = await SignInAsync();
        Factory.Time.Advance(TimeSpan.FromMinutes(20));
        var signedInAgain = await RenewAccessTokenAsync();
        var now = Factory.Time.GetUtcNow();

        using var response = await StepUpAsync(new { password = signedIn.Password });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertNoStore(response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.EnumerateObject().Select(property => property.Name).ShouldBe(["accessToken", "accessTokenExpiresAt", "stepUpExpiresAt"]);
        body.GetProperty("accessTokenExpiresAt").GetDateTimeOffset().ShouldBe(now.AddMinutes(10));
        body.GetProperty("stepUpExpiresAt").GetDateTimeOffset().ShouldBe(now + MaxAge);

        // A new access token of the same session: fresh auth_time, amr what the step-up proved.
        var token = new JsonWebToken(body.GetProperty("accessToken").GetString());
        token.GetClaim("sub").Value.ShouldBe(signedIn.UserId.ToString());
        token.GetClaim("sid").Value.ShouldBe(signedIn.SessionId.ToString());
        Amr(token).ShouldBe(["pwd"]);
        token.GetClaim("auth_time").Value.ShouldBe(now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        new JsonWebToken(signedInAgain.AccessToken).GetClaim("auth_time").Value
            .ShouldBe(now.AddMinutes(-20).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        var session = await QueryAsync(context => context.Set<UserSession>().SingleAsync(candidate => candidate.Id == signedIn.SessionId, Ct));
        session.AuthenticatedAt.ShouldBe(now);
        session.AuthMethods.ShouldBe("pwd");
        session.CreatedAt.ShouldBe(now.AddMinutes(-20));
        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(Ct));
        audit.EventType.ShouldBe(AuthAuditEvents.StepUpSucceeded);
        audit.UserId.ShouldBe(signedIn.UserId);
        audit.SessionId.ShouldBe(signedIn.SessionId);

        // A wrong password is a 400 the client can show, counted towards the lockout, and also not cached.
        using var wrong = await StepUpAsync(new { password = "not the password" });
        await AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.current_password_incorrect");
        AssertNoStore(wrong);
        (await FindUserAsync(signedIn.UserId)).AccessFailedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Step_up_with_totp_returns_amr_otp()
    {
        var signedIn = await SignInAsync();
        var enrollment = await EnrollTotpAsync(signedIn.UserId);
        Factory.Time.Advance(TimeSpan.FromMinutes(2));
        var now = Factory.Time.GetUtcNow();

        // An account with two-step verification must prove the stronger factor: its password is refused and not counted.
        using (var password = await StepUpAsync(new { password = signedIn.Password }))
        {
            await AssertProblemAsync(password, HttpStatusCode.BadRequest, "auth.step_up_method_not_allowed");
        }

        (await FindUserAsync(signedIn.UserId)).AccessFailedCount.ShouldBe(0);

        using var response = await StepUpAsync(new { code = enrollment.CodeAt(now) });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = new JsonWebToken((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString());
        Amr(token).ShouldBe(["otp"]);
        token.GetClaim("auth_time").Value.ShouldBe(now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        var method = await QueryAsync(context => context.Set<UserMfaMethod>().SingleAsync(Ct));
        method.LastUsedTimeStep.ShouldBe(Totp.TimeStepAt(now));
        method.LastUsedAt.ShouldBe(now);
        (await QueryAsync(context => context.Set<UserSession>().SingleAsync(Ct))).AuthMethods.ShouldBe("otp");
    }

    [Fact]
    public async Task Refresh_after_step_up_carries_the_new_auth_time_and_amr()
    {
        var user = await CreateUserAsync(Email);
        var login = await LoginAsync();
        var enrollment = await EnrollTotpAsync(user.Id);
        Factory.Time.Advance(TimeSpan.FromMinutes(5));
        var steppedUpAt = Factory.Time.GetUtcNow();
        using (var stepUp = await StepUpAsync(new { code = enrollment.CodeAt(steppedUpAt) }, login.AccessToken))
        {
            stepUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Factory.Time.Advance(TimeSpan.FromMinutes(3));

        using var refresh = await Client.PostAsJsonAsync(RefreshRoute, new { refreshToken = login.RefreshToken }, Ct);

        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await refresh.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("sessionId").GetGuid().ShouldBe(login.SessionId);
        var token = new JsonWebToken(body.GetProperty("accessToken").GetString());
        Amr(token).ShouldBe(["otp"]);
        token.GetClaim("auth_time").Value.ShouldBe(steppedUpAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Same_code_cannot_step_up_twice()
    {
        var signedIn = await SignInAsync();
        var enrollment = await EnrollTotpAsync(signedIn.UserId);
        var code = enrollment.CodeAt(Factory.Time.GetUtcNow());
        using (var first = await StepUpAsync(new { code }))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Factory.Time.Advance(TimeSpan.FromSeconds(5));

        // The step is claimed: the same code again (a replay, or a second tab) is a wrong code, and counts as one.
        using var second = await StepUpAsync(new { code });

        await AssertProblemAsync(second, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
        (await FindUserAsync(signedIn.UserId)).AccessFailedCount.ShouldBe(1);
        var audits = await QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).ToListAsync(Ct));
        audits.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.StepUpSucceeded, AuthAuditEvents.StepUpFailed]);
        audits[1].FailureReason.ShouldBe("auth.invalid_mfa_code");
        audits[1].Details.ShouldNotBeNull().ShouldNotContain(code);
    }

    [Fact]
    public async Task Unreadable_secret_fails_the_code_but_a_recovery_code_still_works()
    {
        var signedIn = await SignInAsync();
        var enrollment = await EnrollTotpAsync(signedIn.UserId);

        // As after a key-ring loss: the stored value was protected under another purpose (the shared one), so it cannot be read here.
        var foreign = Factory.Services.GetRequiredService<ISecretProtector>().Protect(Base32.Encode(enrollment.Secret));
        await QueryAsync(context => context.Set<UserMfaMethod>()
            .Where(method => method.Id == enrollment.MethodId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(method => method.SecretEncrypted, foreign), Ct));

        using (var code = await StepUpAsync(new { code = enrollment.CodeAt(Factory.Time.GetUtcNow()) }))
        {
            await AssertProblemAsync(code, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
        }

        var recoveryCode = enrollment.RecoveryCodes[3];
        using var response = await StepUpAsync(new { recoveryCode = recoveryCode.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = new JsonWebToken((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString());
        Amr(token).ShouldBe(["rcvr"]);
        var hash = RecoveryCodeFormat.Hash(RecoveryCodeFormat.Normalize(recoveryCode)!);
        var used = await QueryAsync(context => context.Set<RecoveryCode>().SingleAsync(code => code.CodeHash == hash, Ct));
        used.UsedAt.ShouldBe(Factory.Time.GetUtcNow());
        (await QueryAsync(context => context.Set<RecoveryCode>().CountAsync(code => code.UsedAt == null, Ct))).ShouldBe(9);
        var outbox = await QueryAsync(context => context.Set<OutboxMessage>().Select(message => message.Type).ToListAsync(Ct));
        outbox.Count(type => type.Contains(nameof(MfaChangedDomainEvent), StringComparison.Ordinal)).ShouldBe(1);

        // The wrong code counted; the recovery code did not reset the count (only a successful sign-in does).
        (await FindUserAsync(signedIn.UserId)).AccessFailedCount.ShouldBe(1);

        // Single use: the same code again is refused.
        using var again = await StepUpAsync(new { recoveryCode });
        await AssertProblemAsync(again, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
    }

    [Fact]
    public async Task Step_up_requires_a_token_and_is_rate_limited()
    {
        using (var anonymous = await Client.PostAsJsonAsync(StepUpRoute, new { password = AuthTestHarness.Password }, Ct))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await anonymous.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("http.401");
        }

        const int PermitLimit = 3;
        await SignInAsync();
        await using var limited = Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:AuthStrictPermitLimit", PermitLimit.ToString(CultureInfo.InvariantCulture)));
        using var client = await CreateClientAsync(limited);

        for (var attempt = 0; attempt < PermitLimit; attempt++)
        {
            using var allowed = await client.PostAsJsonAsync(StepUpRoute, new { password = $"wrong password {attempt}" }, Ct);
            allowed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var response = await client.PostAsJsonAsync(StepUpRoute, new { password = AuthTestHarness.Password }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.Contains("Retry-After").ShouldBeTrue();
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("rate_limit.exceeded");
    }

    [Fact]
    public async Task Step_up_from_an_ended_session_is_401()
    {
        var signedIn = await SignInAsync();
        using (var logout = await Client.PostAsync(LogoutRoute, content: null, Ct))
        {
            logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The access token itself still validates (D9), but its sid names a revoked session: no new token for it.
        using var ended = await StepUpAsync(new { password = signedIn.Password });

        ended.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ended.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("http.401");

        // A token of the same user whose sid names no session at all is refused alike.
        var unknownSession = TestAccessTokens.Issue(Factory.Services, signedIn.UserId).Value;
        using var unknown = await StepUpAsync(new { password = signedIn.Password }, unknownSession);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await FindUserAsync(signedIn.UserId)).AccessFailedCount.ShouldBe(0);
        var events = await QueryAsync(context => context.Set<AuthAuditLog>().Select(entry => entry.EventType).ToListAsync(Ct));
        events.ShouldBe([AuthAuditEvents.Logout]);
    }

    [Fact]
    public async Task Request_without_exactly_one_proof_is_a_validation_problem()
    {
        await SignInAsync();

        using var none = await StepUpAsync(new { });
        using var two = await StepUpAsync(new { password = AuthTestHarness.Password, code = "123456" });

        foreach (var response in new[] { none, two })
        {
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation.failed");
        }
    }

    private static List<string> Amr(JsonWebToken token) => [.. token.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value)];

    private static void AssertNoStore(HttpResponseMessage response)
    {
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        response.Headers.Pragma.ToString().ShouldBe("no-cache");
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe(code);
    }

    private async Task<HttpResponseMessage> StepUpAsync(object body, string? accessToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, StepUpRoute) { Content = JsonContent.Create(body) };
        if (accessToken is not null)
        {
            request.WithBearer(accessToken);
        }

        return await Client.SendAsync(request, Ct);
    }

    private async Task<Tokens> LoginAsync()
    {
        using var response = await Client.PostAsJsonAsync(LoginRoute, new { email = Email, password = AuthTestHarness.Password }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!, body.GetProperty("sessionId").GetGuid());
    }

    private Task<User> FindUserAsync(Guid userId)
        => QueryAsync(context => context.Set<User>().AsNoTracking().SingleAsync(user => user.Id == userId, Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);
}
