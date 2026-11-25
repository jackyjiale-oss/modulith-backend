using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TemplateName.Application.Common.Security;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// Signing in with a code texted to the account's verified number, on the real host: the session and its <c>amr</c>, the challenge of a
/// TOTP account, the step-up an SMS session cannot satisfy, the denied roles, the one 400 of every refusal, the identical 202 of every
/// send, the lockout, the races and what is never stored or logged. Every request comes from its own client address unless a test says
/// otherwise, so the per-address limit only bites where a test means it to.
/// </summary>
public sealed class PhoneSignInTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string SendRoute = "/api/v1/auth/phone/sign-in/send";
    private const string VerifyRoute = "/api/v1/auth/phone/sign-in/verify";
    private const string MfaVerifyRoute = "/api/v1/auth/mfa/verify";
    private const string StepUpRoute = "/api/v1/auth/step-up";
    private const string TotpSetupRoute = "/api/v1/auth/mfa/totp/setup";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string MeRoute = "/api/v1/auth/me";
    private const string Number = "+60123456789";
    private const string OtherNumber = "+60198765432";

    private int _addressCount;

    [Fact]
    public async Task Send_and_verify_sign_in_with_amr_sms()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        var user = await CreateUserWithNumberAsync("alice@example.com", Number);

        using var sent = await PostAsync(client, SendRoute, new { phoneNumber = "+60 12-345 6789" });
        sent.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var verified = await PostAsync(client, VerifyRoute, new { phoneNumber = Number, code = await LatestCodeAsync(host, user.Id), deviceName = "Pixel 9" });

        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await verified.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeFalse();
        (await SessionAsync(body.GetProperty("sessionId").GetGuid())).AuthMethods.ShouldBe("sms");
        using var me = new HttpRequestMessage(HttpMethod.Get, MeRoute);
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        (await client.SendAsync(me, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Known_and_unknown_numbers_get_identical_202s()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        await CreateUserWithNumberAsync("alice@example.com", Number);

        using var known = await PostAsync(client, SendRoute, new { phoneNumber = Number });
        using var unknown = await PostAsync(client, SendRoute, new { phoneNumber = OtherNumber });

        known.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        ProblemResponseComparison.Headers(unknown).ShouldBe(ProblemResponseComparison.Headers(known));
        (await unknown.Content.ReadAsStringAsync(Ct)).ShouldBe(await known.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Every_verify_refusal_is_one_body()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        var alice = await CreateUserWithNumberAsync("alice@example.com", Number);
        var bob = await CreateUserWithNumberAsync("bob@example.com", OtherNumber);
        (await PostAsync(client, SendRoute, new { phoneNumber = Number })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await PostAsync(client, SendRoute, new { phoneNumber = OtherNumber })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var aliceCode = await LatestCodeAsync(host, alice.Id);

        using var unknown = await PostAsync(client, VerifyRoute, new { phoneNumber = "+60111111111", code = "123456" });
        using var wrong = await PostAsync(client, VerifyRoute, new { phoneNumber = Number, code = aliceCode == "000000" ? "000001" : "000000" });
        await LockAsync(bob.Id);
        using var locked = await PostAsync(client, VerifyRoute, new { phoneNumber = OtherNumber, code = await LatestCodeAsync(host, bob.Id) });
        Factory.Time.Advance(TimeSpan.FromMinutes(6));
        using var expired = await PostAsync(client, VerifyRoute, new { phoneNumber = Number, code = aliceCode });

        await ProblemResponseComparison.AssertIdenticalAsync(HttpStatusCode.BadRequest, "auth.invalid_code", unknown, wrong, locked, expired);
    }

    [Fact]
    public async Task Totp_account_gets_a_challenge_then_sms_otp_mfa()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        var user = await CreateUserWithNumberAsync("alice@example.com", Number);
        var enrollment = await EnrollTotpAsync(user.Id);

        var challenge = await SignInBySmsAsync(host, client, user.Id);

        challenge.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        using var verified = await PostAsync(
            client,
            MfaVerifyRoute,
            new { challengeToken = challenge.GetProperty("challengeToken").GetString(), code = enrollment.CodeAt(Factory.Time.GetUtcNow()) });
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await verified.Content.ReadFromJsonAsync<JsonElement>(Ct);
        (await SessionAsync(body.GetProperty("sessionId").GetGuid())).AuthMethods.ShouldBe("sms otp mfa");
    }

    [Fact]
    public async Task An_sms_session_needs_a_step_up_for_totp_setup()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        var user = await CreateUserWithNumberAsync("alice@example.com", Number);
        var signedIn = await SignInBySmsAsync(host, client, user.Id);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.GetProperty("accessToken").GetString());

        using var refused = await client.PostAsJsonAsync(TotpSetupRoute, new { }, Ct);
        await AssertProblemAsync(refused, HttpStatusCode.Forbidden, "auth.step_up_required");

        using var steppedUp = await client.PostAsJsonAsync(StepUpRoute, new { password = AuthTestHarness.Password }, Ct);
        steppedUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await steppedUp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var setUp = await client.PostAsJsonAsync(TotpSetupRoute, new { }, Ct);
        setUp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_without_totp_is_denied_and_with_totp_challenged()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        var admin = await CreateUserWithNumberAsync("admin@example.com", Number);
        await AssignRoleAsync(admin.Id, SystemRoles.Admin);

        (await PostAsync(client, SendRoute, new { phoneNumber = Number })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await CodeCountAsync(admin.Id)).ShouldBe(0);

        await EnrollTotpAsync(admin.Id);
        Factory.Time.Advance(TimeSpan.FromMinutes(2));
        var challenge = await SignInBySmsAsync(host, client, admin.Id);
        challenge.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        var user = await CreateUserWithNumberAsync("alice@example.com", Number);
        (await PostAsync(client, SendRoute, new { phoneNumber = Number })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var code = await LatestCodeAsync(host, user.Id);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await PostAsync(client, VerifyRoute, new { phoneNumber = Number, code = wrong });
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.invalid_code");
        }

        (await UserAsync(user.Id)).IsLockedOut(Factory.Time.GetUtcNow()).ShouldBeTrue();
        using var login = await PostAsync(client, LoginRoute, new { email = "alice@example.com", password = AuthTestHarness.Password });
        await AssertProblemAsync(login, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
    }

    [Fact]
    public async Task Two_verifies_with_one_code_give_one_session()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        var user = await CreateUserWithNumberAsync("alice@example.com", Number);
        (await PostAsync(client, SendRoute, new { phoneNumber = Number })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var code = await LatestCodeAsync(host, user.Id);

        var responses = await Task.WhenAll(
            PostAsync(client, VerifyRoute, new { phoneNumber = Number, code }),
            PostAsync(client, VerifyRoute, new { phoneNumber = Number, code }));

        responses.Select(response => response.StatusCode).ShouldBe([HttpStatusCode.OK, HttpStatusCode.BadRequest], ignoreOrder: true);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Two_sends_at_once_issue_one_code()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        var user = await CreateUserWithNumberAsync("alice@example.com", Number);

        var responses = await Task.WhenAll(
            PostAsync(client, SendRoute, new { phoneNumber = Number }),
            PostAsync(client, SendRoute, new { phoneNumber = Number }));

        responses.Select(response => response.StatusCode).ShouldBe([HttpStatusCode.Accepted, HttpStatusCode.Accepted]);
        (await CodeCountAsync(user.Id)).ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_number_change_kills_the_pending_code()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        var user = await CreateUserWithNumberAsync("alice@example.com", Number);
        (await PostAsync(client, SendRoute, new { phoneNumber = Number })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var code = await LatestCodeAsync(host, user.Id);
        await SetNumberAsync(user.Id, OtherNumber);

        using var response = await PostAsync(client, VerifyRoute, new { phoneNumber = OtherNumber, code });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.invalid_code");
    }

    [Fact]
    public async Task Verify_is_no_store()
    {
        await using var host = SignInHost();
        using var client = host.CreateClient();
        var user = await CreateUserWithNumberAsync("alice@example.com", Number);
        (await PostAsync(client, SendRoute, new { phoneNumber = Number })).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var refused = await PostAsync(client, VerifyRoute, new { phoneNumber = "+60111111111", code = "123456" });
        using var signedIn = await PostAsync(client, VerifyRoute, new { phoneNumber = Number, code = await LatestCodeAsync(host, user.Id) });

        AssertNoStore(refused);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertNoStore(signedIn);
    }

    [Fact]
    public async Task Send_and_verify_are_rate_limited()
    {
        const int PermitLimit = 2;
        const string Address = "203.0.113.60";
        await using var host = SignInHost(builder =>
            builder.UseSetting("RateLimiting:AuthStrictPermitLimit", PermitLimit.ToString(CultureInfo.InvariantCulture)));
        using var client = host.CreateClient();

        for (var attempt = 0; attempt < PermitLimit; attempt++)
        {
            (await PostAsync(client, SendRoute, new { phoneNumber = Number }, Address)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        using var limitedSend = await PostAsync(client, SendRoute, new { phoneNumber = Number }, Address);
        using var limitedVerify = await PostAsync(client, VerifyRoute, new { phoneNumber = Number, code = "123456" }, Address);

        await AssertProblemAsync(limitedSend, HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
        await AssertProblemAsync(limitedVerify, HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
    }

    [Fact]
    public async Task Routes_are_404_while_sign_in_is_off()
    {
        await using var host = Factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:Phone:Enabled", "true"));
        using var client = host.CreateClient();

        using var send = await PostAsync(client, SendRoute, new { phoneNumber = Number });
        using var verify = await PostAsync(client, VerifyRoute, new { phoneNumber = Number, code = "123456" });

        await AssertProblemAsync(send, HttpStatusCode.NotFound, "http.404");
        await AssertProblemAsync(verify, HttpStatusCode.NotFound, "http.404");
    }

    [Fact]
    public async Task The_code_never_reaches_a_column_or_a_log_and_the_number_no_log()
    {
        var logs = new RecordingLoggerProvider();
        await using var host = SignInHost(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
        }));
        using var client = host.CreateClient();
        var user = await CreateUserWithNumberAsync("alice@example.com", Number);

        (await PostAsync(client, SendRoute, new { phoneNumber = Number })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var code = await LatestCodeAsync(host, user.Id);
        using var wrong = await PostAsync(client, VerifyRoute, new { phoneNumber = Number, code = code == "000000" ? "000001" : "000000" });
        using var right = await PostAsync(client, VerifyRoute, new { phoneNumber = Number, code });
        right.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await DatabaseTextScanner.ColumnsContainingAsync(Factory.Services, [code], Ct)).ShouldBeEmpty();
        logs.Entries.ShouldNotBeEmpty();
        logs.Entries.ShouldAllBe(entry => !entry.Contains(code, StringComparison.Ordinal) && !entry.Contains("60123456789", StringComparison.Ordinal));
    }

    private WebApplicationFactory<Program> SignInHost(Action<IWebHostBuilder>? configure = null)
        => Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Phone:Enabled", "true");
            builder.UseSetting("Auth:Phone:SignInEnabled", "true");
            configure?.Invoke(builder);
        });

    /// <summary>Sends a code to the user's number and verifies it; the body of the verify (tokens or a challenge).</summary>
    private async Task<JsonElement> SignInBySmsAsync(WebApplicationFactory<Program> host, HttpClient client, Guid userId)
    {
        var number = (await UserAsync(userId)).PhoneNumber!;
        (await PostAsync(client, SendRoute, new { phoneNumber = number })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var verified = await PostAsync(client, VerifyRoute, new { phoneNumber = number, code = await LatestCodeAsync(host, userId) });
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await verified.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<User> CreateUserWithNumberAsync(string email, string number)
    {
        var user = await CreateUserAsync(email);
        await SetNumberAsync(user.Id, number);
        return user;
    }

    private async Task SetNumberAsync(Guid userId, string number)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await context.Set<User>().SingleAsync(candidate => candidate.Id == userId, Ct);
        user.VerifyPhoneNumber(number, Factory.Time.GetUtcNow());
        await context.SaveChangesAsync(Ct);
    }

    private async Task AssignRoleAsync(Guid userId, string roleName)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var role = await context.Set<Role>().SingleAsync(candidate => candidate.NormalizedName == Role.NormalizeName(roleName), Ct);
        var user = await context.Set<User>().SingleAsync(candidate => candidate.Id == userId, Ct);
        user.AssignRole(role.Id, assignedBy: null, Factory.Time.GetUtcNow()).IsSuccess.ShouldBeTrue();
        await context.SaveChangesAsync(Ct);
    }

    private async Task LockAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await context.Set<User>().SingleAsync(candidate => candidate.Id == userId, Ct);
        user.RecordFailedSignIn(Factory.Time.GetUtcNow(), maxFailedAttempts: 1, TimeSpan.FromMinutes(15));
        await context.SaveChangesAsync(Ct);
    }

    private async Task<User> UserAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<User>().AsNoTracking().SingleAsync(user => user.Id == userId, Ct);
    }

    private async Task<UserSession> SessionAsync(Guid sessionId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<UserSession>().AsNoTracking()
            .SingleAsync(session => session.Id == sessionId, Ct);
    }

    private async Task<int> CodeCountAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<PhoneVerificationCode>()
            .CountAsync(code => code.UserId == userId && code.Purpose == PhoneCodePurpose.SignIn, Ct);
    }

    /// <summary>The plain code of <paramref name="userId"/>'s newest sign-in code, decrypted from its outbox row as the consumer would.</summary>
    private async Task<string> LatestCodeAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(row => row.Type == typeof(PhoneSignInCodeIssuedDomainEvent).FullName)
            .OrderByDescending(row => row.OccurredAt)
            .ToListAsync(Ct);
        foreach (var message in messages)
        {
            using var content = JsonDocument.Parse(message.Content);
            var properties = content.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            if (properties["UserId"].GetGuid() == userId)
            {
                return scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(properties["ProtectedCode"].GetString()!);
            }
        }

        throw new InvalidOperationException("No sign-in code was sent to the user.");
    }

    /// <summary>Posts <paramref name="body"/> anonymously from <paramref name="address"/>, or from an address no request used before.</summary>
    private async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object body, string? address = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, address ?? $"198.51.100.{Interlocked.Increment(ref _addressCount)}");
        return await client.SendAsync(request, Ct);
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        response.Headers.Pragma.ToString().ShouldBe("no-cache");
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(code);
    }
}
