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
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// <c>POST /api/v1/auth/login</c> through the running host: tokens and session, the generic answer for every wrong combination
/// (enumeration), lockout on <see cref="IntegrationTestWebAppFactory.Time"/>, the 403s after a correct password, audit rows and the
/// per-address rate limit.
/// </summary>
public sealed class LoginTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string LoginRoute = "/api/v1/auth/login";
    private const string MeRoute = "/api/v1/auth/me";
    private const string Email = "alice@example.com";
    private const string Password = AuthTestHarness.Password;
    private const string WrongPassword = "not-the-right-password";
    private const string ClientAddress = "203.0.113.9";
    private const string UserAgent = "LoginTests/1.0";

    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly string[] VolatileHeaders = ["X-Trace-Id", "Date"];

    [Fact]
    public async Task Login_succeeds_and_the_access_token_authenticates_get_me()
    {
        var user = await CreateUserAsync(Email);
        var now = Factory.Time.GetUtcNow();

        using var response = await LoginAsync(Email, Password, deviceName: "Alice's laptop");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        PropertyNames(body).ShouldBe(["accessToken", "accessTokenExpiresAt", "refreshToken", "refreshTokenExpiresAt", "sessionId"], ignoreOrder: true);
        var refreshToken = body.GetProperty("refreshToken").GetString().ShouldNotBeNull();
        refreshToken.Length.ShouldBe(43);
        body.GetProperty("accessTokenExpiresAt").GetDateTimeOffset().ShouldBe(now.AddMinutes(10));
        body.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset().ShouldBe(now.AddDays(14));
        var sessionId = body.GetProperty("sessionId").GetGuid();

        // Only the hash of the refresh token is stored, in the new session's first token.
        var session = await QueryAsync(context => context.Set<UserSession>().Include(candidate => candidate.RefreshTokens).SingleAsync(Ct));
        session.Id.ShouldBe(sessionId);
        session.UserId.ShouldBe(user.Id);
        session.DeviceName.ShouldBe("Alice's laptop");
        session.AuthMethods.ShouldBe("pwd");
        session.ExpiresAt.ShouldBe(now.AddDays(90));
        session.IpAddress.ShouldBe(ClientAddress);
        session.UserAgent.ShouldBe(UserAgent);
        session.RefreshTokens.ShouldHaveSingleItem().TokenHash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

        var stored = await FindUserAsync(Email);
        stored.LastLoginAt.ShouldBe(now);
        stored.AccessFailedCount.ShouldBe(0);

        using var me = new HttpRequestMessage(HttpMethod.Get, MeRoute).WithBearer(body.GetProperty("accessToken").GetString()!);
        using var meResponse = await Client.SendAsync(me, Ct);

        meResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var meBody = await meResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
        meBody.GetProperty("id").GetGuid().ShouldBe(user.Id);
        meBody.GetProperty("email").GetString().ShouldBe(Email);
        meBody.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ShouldBe(["User"]);
    }

    [Fact]
    public async Task Device_name_defaults_to_Unknown()
    {
        await CreateUserAsync(Email);

        using var response = await LoginAsync(Email, Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await QueryAsync(context => context.Set<UserSession>().SingleAsync(Ct))).DeviceName.ShouldBe("Unknown");
    }

    [Fact]
    public async Task Access_token_claims_match_the_session()
    {
        var user = await CreateUserAsync(Email, locale: "ms");
        var now = Factory.Time.GetUtcNow();

        using var response = await LoginAsync(Email, Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var token = new JsonWebToken(body.GetProperty("accessToken").GetString());
        token.GetClaim("sub").Value.ShouldBe(user.Id.ToString());
        token.GetClaim("sid").Value.ShouldBe(body.GetProperty("sessionId").GetGuid().ToString());
        token.GetClaim("sst").Value.ShouldBe(user.SecurityStamp);
        token.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value).ShouldBe(["pwd"]);
        token.GetClaim("locale").Value.ShouldBe("ms");
        token.GetClaim("auth_time").Value.ShouldBe(now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        token.ValidTo.ShouldBe(body.GetProperty("accessTokenExpiresAt").GetDateTimeOffset().UtcDateTime);
        var session = await QueryAsync(context => context.Set<UserSession>().SingleAsync(Ct));
        session.SecurityStamp.ShouldBe(user.SecurityStamp);
    }

    [Fact]
    public async Task Wrong_password_returns_401_invalid_credentials()
    {
        await CreateUserAsync(Email);

        using var response = await LoginAsync(Email, WrongPassword);

        await AssertInvalidCredentialsAsync(response);
        (await QueryAsync(context => context.Set<UserSession>().CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Login_accepts_any_case_and_surrounding_white_space_of_the_email()
    {
        await CreateUserAsync(Email);

        using var response = await LoginAsync("  ALICE@Example.com ", Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_return_identical_responses()
    {
        await CreateUserAsync(Email);

        using var wrongPassword = await LoginAsync(Email, WrongPassword);
        using var unknownEmail = await LoginAsync("nobody@example.com", WrongPassword);

        await AssertIdenticalAsync(wrongPassword, unknownEmail);
    }

    [Fact]
    public async Task User_without_password_hash_gets_the_same_response_as_unknown_email()
    {
        await CreateUserAsync(Email, password: null);

        using var withoutPassword = await LoginAsync(Email, Password);
        using var unknownEmail = await LoginAsync("nobody@example.com", Password);

        await AssertIdenticalAsync(withoutPassword, unknownEmail);

        // Like an unknown email, it counts nothing.
        var stored = await FindUserAsync(Email);
        stored.AccessFailedCount.ShouldBe(0);
        stored.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public async Task Lockout_after_five_failures_blocks_even_the_correct_password()
    {
        await CreateUserAsync(Email);
        await FailAsync(Email, times: 5);

        using var response = await LoginAsync(Email, Password);

        await AssertInvalidCredentialsAsync(response);
        var stored = await FindUserAsync(Email);
        stored.AccessFailedCount.ShouldBe(5);
        stored.LockoutEnd.ShouldBe(Factory.Time.GetUtcNow() + LockoutDuration);
        (await QueryAsync(context => context.Set<UserSession>().CountAsync(Ct))).ShouldBe(0);
        (await AuditEventsAsync()).ShouldBe(
        [
            AuthAuditEvents.LoginFailed,
            AuthAuditEvents.LoginFailed,
            AuthAuditEvents.LoginFailed,
            AuthAuditEvents.LoginFailed,
            AuthAuditEvents.LoginFailed,
            AuthAuditEvents.LockedOut,
            AuthAuditEvents.LoginFailed,
        ]);
        var lastFailure = await QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).LastAsync(Ct));
        lastFailure.FailureReason.ShouldBe("locked");
    }

    [Fact]
    public async Task Lockout_ends_exactly_at_the_lockout_time()
    {
        await CreateUserAsync(Email);
        await FailAsync(Email, times: 5);
        var lockoutEnd = (await FindUserAsync(Email)).LockoutEnd.ShouldNotBeNull();

        Factory.Time.Advance(lockoutEnd - Factory.Time.GetUtcNow() - TimeSpan.FromTicks(1));
        using (var stillLocked = await LoginAsync(Email, Password))
        {
            await AssertInvalidCredentialsAsync(stillLocked);
        }

        Factory.Time.Advance(TimeSpan.FromTicks(1));
        Factory.Time.GetUtcNow().ShouldBe(lockoutEnd);
        using var response = await LoginAsync(Email, Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await FindUserAsync(Email);
        stored.AccessFailedCount.ShouldBe(0);
        stored.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public async Task Parallel_wrong_passwords_are_all_counted_and_lock_the_account()
    {
        const int Attempts = 10;
        var user = await CreateUserAsync(Email);

        // Ten wrong passwords at once, each from its own address, all verified before any of them saves. The database counts them one
        // at a time: the first five raise the count to 5, the fifth sets the lockout, and the other five find the account locked, which
        // changes nothing (User.RecordFailedSignIn). The clock does not move, so this arithmetic does not depend on the interleaving.
        var responses = await Task.WhenAll(Enumerable.Range(1, Attempts)
            .Select(attempt => LoginAsync(Client, Email, WrongPassword, address: $"203.0.113.{attempt}")));

        try
        {
            // Every answer is the same generic 401: same status, headers and body but for the trace id.
            var answers = new List<(HttpStatusCode Status, Dictionary<string, string> Headers, string Body)>();
            foreach (var response in responses)
            {
                var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
                body.GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
                var members = body.EnumerateObject().Where(member => member.Name != "traceId").Select(member => $"{member.Name}={member.Value.GetRawText()}");
                answers.Add((response.StatusCode, Headers(response), string.Join("|", members)));
            }

            answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.Unauthorized);
            answers.Select(answer => answer.Body).Distinct().ShouldHaveSingleItem();
            answers.ShouldAllBe(answer => answer.Headers.Count == answers[0].Headers.Count
                && answer.Headers.All(header => answers[0].Headers.GetValueOrDefault(header.Key) == header.Value));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        var stored = await FindUserAsync(Email);
        stored.AccessFailedCount.ShouldBe(5);
        stored.LockoutEnd.ShouldBe(Factory.Time.GetUtcNow() + LockoutDuration);
        var entries = await QueryAsync(context => context.Set<AuthAuditLog>().Where(entry => entry.UserId == user.Id).ToListAsync(Ct));
        entries.Count(entry => entry.EventType == AuthAuditEvents.LoginFailed).ShouldBe(Attempts);
        entries.Count(entry => entry.EventType == AuthAuditEvents.LockedOut).ShouldBe(1);
        entries.Where(entry => entry.EventType == AuthAuditEvents.LoginFailed).Select(entry => entry.IpAddress).Distinct().Count().ShouldBe(Attempts);
        var lockoutMessages = await QueryAsync(context => context.Set<OutboxMessage>()
            .CountAsync(message => message.Type.Contains(nameof(UserLockedOutDomainEvent)), Ct));
        lockoutMessages.ShouldBe(1);

        // Locked for the right password too.
        using var correct = await LoginAsync(Email, Password);
        await AssertInvalidCredentialsAsync(correct);
    }

    [Fact]
    public async Task Failed_login_counter_survives_the_failure_result()
    {
        await CreateUserAsync(Email);

        await FailAsync(Email, times: 2);

        var stored = await FindUserAsync(Email);
        stored.AccessFailedCount.ShouldBe(2);
        stored.LockoutEnd.ShouldBeNull();
        (await AuditEventsAsync()).ShouldBe([AuthAuditEvents.LoginFailed, AuthAuditEvents.LoginFailed]);
    }

    [Fact]
    public async Task Unverified_email_returns_403_email_not_verified()
    {
        await CreateUserAsync(Email, confirmed: false);

        // A wrong password says nothing about the account state: still the generic 401.
        using (var wrongPassword = await LoginAsync(Email, WrongPassword))
        {
            await AssertInvalidCredentialsAsync(wrongPassword);
        }

        using var response = await LoginAsync(Email, Password);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "auth.email_not_verified");
        (await QueryAsync(context => context.Set<UserSession>().CountAsync(Ct))).ShouldBe(0);
        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).LastAsync(Ct));
        audit.EventType.ShouldBe(AuthAuditEvents.LoginFailed);
        audit.FailureReason.ShouldBe("auth.email_not_verified");
    }

    [Fact]
    public async Task Suspended_user_returns_403_account_inactive()
    {
        await CreateUserAsync(Email, suspended: true);

        using var response = await LoginAsync(Email, Password);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "auth.account_inactive");
        (await QueryAsync(context => context.Set<UserSession>().CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Login_writes_audit_rows_for_success_and_failure()
    {
        var user = await CreateUserAsync(Email);

        using var failed = await LoginAsync(Email, WrongPassword);
        using var unknown = await LoginAsync("nobody@example.com", WrongPassword);
        using var succeeded = await LoginAsync(Email, Password);

        succeeded.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessionId = (await succeeded.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sessionId").GetGuid();
        var entries = await QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).ToListAsync(Ct));
        entries.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.LoginFailed, AuthAuditEvents.LoginFailed, AuthAuditEvents.LoginSucceeded]);

        var (failure, unknownFailure, success) = (entries[0], entries[1], entries[2]);
        failure.Succeeded.ShouldBeFalse();
        failure.UserId.ShouldBe(user.Id);
        failure.FailureReason.ShouldBe("auth.invalid_credentials");
        failure.AttemptedIdentifier.ShouldBe("a****@example.com");
        failure.TraceId.ShouldBe(TraceId(failed));
        unknownFailure.UserId.ShouldBeNull();
        unknownFailure.AttemptedIdentifier.ShouldBe("n****@example.com");
        unknownFailure.TraceId.ShouldBe(TraceId(unknown));
        success.Succeeded.ShouldBeTrue();
        success.UserId.ShouldBe(user.Id);
        success.SessionId.ShouldBe(sessionId);
        success.AttemptedIdentifier.ShouldBe("a****@example.com");
        success.TraceId.ShouldBe(TraceId(succeeded));
        entries.ShouldAllBe(entry => entry.IpAddress == ClientAddress && entry.UserAgent == UserAgent);

        // No column of any row holds the password, typed right or wrong.
        foreach (var entry in entries)
        {
            string?[] texts = [entry.AttemptedIdentifier, entry.FailureReason, entry.Details, entry.UserAgent];
            texts.ShouldAllBe(text => text == null || (!text.Contains(Password) && !text.Contains(WrongPassword)));
        }
    }

    [Fact]
    public async Task Login_is_rate_limited_per_client_address()
    {
        const int PermitLimit = 3;
        await using var limited = Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:AuthStrictPermitLimit", PermitLimit.ToString(CultureInfo.InvariantCulture)));
        using var client = limited.CreateClient();

        for (var attempt = 0; attempt < PermitLimit; attempt++)
        {
            using var allowed = await LoginAsync(client, $"user{attempt}@example.com", WrongPassword, address: "203.0.113.1");
            allowed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var limitedResponse = await LoginAsync(client, Email, WrongPassword, address: "203.0.113.1");
        using var otherAddress = await LoginAsync(client, Email, WrongPassword, address: "203.0.113.2");

        limitedResponse.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limitedResponse.Headers.Contains("Retry-After").ShouldBeTrue();
        (await limitedResponse.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("rate_limit.exceeded");
        otherAddress.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(129)]
    [InlineData(1024 * 1024)]
    public async Task Oversized_password_is_rejected_with_400_and_changes_nothing(int length)
    {
        await CreateUserAsync(Email);

        using var response = await LoginAsync(Email, new string('p', length));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("errors").TryGetProperty("password", out _).ShouldBeTrue();
        body.GetRawText().ShouldNotContain("pppppppppppp");
        (await FindUserAsync(Email)).AccessFailedCount.ShouldBe(0);
        (await QueryAsync(context => context.Set<AuthAuditLog>().CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Device_name_longer_than_200_characters_is_rejected()
    {
        await CreateUserAsync(Email);

        using var response = await LoginAsync(Email, Password, deviceName: new string('d', 201));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("deviceName", out _).ShouldBeTrue();
    }

    private static string TraceId(HttpResponseMessage response) => response.Headers.GetValues("X-Trace-Id").Single();

    private static List<string> PropertyNames(JsonElement body) => [.. body.EnumerateObject().Select(property => property.Name)];

    private static Dictionary<string, string> Headers(HttpResponseMessage response)
        => response.Headers.Concat(response.Content.Headers)
            .Where(header => !VolatileHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);

    private static async Task AssertInvalidCredentialsAsync(HttpResponseMessage response)
        => await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "auth.invalid_credentials");

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe(code);
        body.GetProperty("traceId").GetString().ShouldBe(TraceId(response));
    }

    /// <summary>The same status, headers (but the trace id and date), body fields, <c>code</c>, <c>title</c>, <c>type</c> and <c>detail</c>.</summary>
    private static async Task AssertIdenticalAsync(HttpResponseMessage first, HttpResponseMessage second)
    {
        first.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        second.StatusCode.ShouldBe(first.StatusCode);
        Headers(second).ShouldBe(Headers(first));

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>(Ct);
        firstBody.GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
        firstBody.GetProperty("traceId").GetString().ShouldBe(TraceId(first));
        secondBody.GetProperty("traceId").GetString().ShouldBe(TraceId(second));
        PropertyNames(secondBody).ShouldBe(PropertyNames(firstBody));
        foreach (var name in PropertyNames(firstBody).Where(name => name != "traceId"))
        {
            secondBody.GetProperty(name).GetRawText().ShouldBe(firstBody.GetProperty(name).GetRawText(), name);
        }
    }

    private async Task FailAsync(string email, int times)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            using var response = await LoginAsync(email, WrongPassword);
            await AssertInvalidCredentialsAsync(response);
        }
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password, string? deviceName = null)
        => LoginAsync(Client, email, password, ClientAddress, deviceName);

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password, string address, string? deviceName = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginRoute)
        {
            Content = JsonContent.Create(new { email, password, deviceName }),
        };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, address);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return await client.SendAsync(request, Ct);
    }

    private Task<User> FindUserAsync(string email)
        => QueryAsync(context => context.Set<User>().SingleAsync(user => user.NormalizedEmail == User.NormalizeEmail(email), Ct));

    private Task<List<string>> AuditEventsAsync()
        => QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).Select(entry => entry.EventType).ToListAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }
}
