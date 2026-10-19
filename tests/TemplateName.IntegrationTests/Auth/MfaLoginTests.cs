using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Infrastructure.Observability;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The sign-in of an account with two-step verification through the running host: <c>POST /api/v1/auth/login</c> answers a challenge
/// instead of tokens, and <c>POST /api/v1/auth/mfa/verify</c> trades it and a code for the tokens. Covers the enumeration guarantees,
/// the three guess limits (per challenge, the user's lockout, the per-address rate limit), simultaneous and repeated codes, recovery
/// codes, challenge misuse and what is never logged, audited or tagged. Every request comes from its own client address unless a test
/// says otherwise, so the strict limit never decides an outcome by accident.
/// </summary>
public sealed class MfaLoginTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string LoginRoute = "/api/v1/auth/login";
    private const string VerifyRoute = "/api/v1/auth/mfa/verify";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string MeRoute = "/api/v1/auth/me";
    private const string MethodsRoute = "/api/v1/auth/mfa/methods";
    private const string Email = "alice@example.com";
    private const string WrongPassword = "not-the-right-password";

    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private int _addressCount;

    [Fact]
    public async Task Mfa_login_returns_a_challenge_and_verify_returns_tokens()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var now = Factory.Time.GetUtcNow();

        using var login = await LoginAsync(Email, deviceName: "Alice's laptop");

        // The password was right, so the answer is a challenge: no token member, the challenge members only, never cached.
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertNoStore(login);
        var challengeBody = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        PropertyNames(challengeBody).ShouldBe(["mfaRequired", "challengeToken", "challengeExpiresAt", "methods"], ignoreOrder: true);
        challengeBody.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        var challengeToken = challengeBody.GetProperty("challengeToken").GetString().ShouldNotBeNull();
        challengeToken.Length.ShouldBe(43);
        challengeBody.GetProperty("challengeExpiresAt").GetDateTimeOffset().ShouldBe(now + ChallengeLifetime);
        challengeBody.GetProperty("methods").EnumerateArray().Select(method => method.GetString()).ShouldBe(["totp", "recovery_code"]);

        // Only the hash of the challenge token is stored, bound to the user's stamp; no session exists yet and the sign-in is not recorded.
        var challenge = await QueryAsync(context => context.Set<MfaChallenge>().AsNoTracking().SingleAsync(Ct));
        challenge.TokenHash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(challengeToken)));
        challenge.UserId.ShouldBe(user.Id);
        challenge.SecurityStamp.ShouldBe(user.SecurityStamp);
        challenge.DeviceName.ShouldBe("Alice's laptop");
        (await QueryAsync(context => context.Set<UserSession>().CountAsync(Ct))).ShouldBe(0);
        (await FindUserAsync(user.Id)).LastLoginAt.ShouldBeNull();

        Factory.Time.Advance(TimeSpan.FromSeconds(20));
        var verifiedAt = Factory.Time.GetUtcNow();
        using var verify = await VerifyAsync(challengeToken, code: enrollment.CodeAt(verifiedAt));

        // The tokens, and no challenge member.
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertNoStore(verify);
        var tokens = await verify.Content.ReadFromJsonAsync<JsonElement>(Ct);
        PropertyNames(tokens).ShouldBe(["accessToken", "accessTokenExpiresAt", "refreshToken", "refreshTokenExpiresAt", "sessionId"], ignoreOrder: true);
        tokens.GetProperty("accessTokenExpiresAt").GetDateTimeOffset().ShouldBe(verifiedAt.AddMinutes(10));
        tokens.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset().ShouldBe(verifiedAt.AddDays(14));
        var sessionId = tokens.GetProperty("sessionId").GetGuid();
        var accessToken = new JsonWebToken(tokens.GetProperty("accessToken").GetString());
        Amr(accessToken).ShouldBe(["pwd", "otp", "mfa"]);
        accessToken.GetClaim("auth_time").Value.ShouldBe(verifiedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        accessToken.GetClaim("sid").Value.ShouldBe(sessionId.ToString());

        var session = await QueryAsync(context => context.Set<UserSession>().AsNoTracking().SingleAsync(Ct));
        session.Id.ShouldBe(sessionId);
        session.AuthMethods.ShouldBe("pwd otp mfa");
        session.AuthenticatedAt.ShouldBe(verifiedAt);
        session.DeviceName.ShouldBe("Alice's laptop");
        (await QueryAsync(context => context.Set<MfaChallenge>().AsNoTracking().SingleAsync(Ct))).ConsumedAt.ShouldBe(verifiedAt);
        (await FindUserAsync(user.Id)).LastLoginAt.ShouldBe(verifiedAt);
        (await AuditEventsAsync()).ShouldBe([AuthAuditEvents.MfaChallengeIssued, AuthAuditEvents.MfaVerified, AuthAuditEvents.LoginSucceeded]);

        // The tokens work like any sign-in's: GET me, and a refresh that keeps the session's amr.
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, MeRoute).WithBearer(accessToken.EncodedToken);
        using var me = await Client.SendAsync(meRequest, Ct);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await me.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid().ShouldBe(user.Id);
        using var refresh = await PostAsync(Client, RefreshRoute, new { refreshToken = tokens.GetProperty("refreshToken").GetString() });
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        Amr(new JsonWebToken((await refresh.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString())).ShouldBe(["pwd", "otp", "mfa"]);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_for_an_mfa_account_return_identical_responses()
    {
        var user = await CreateUserAsync(Email);
        await EnrollTotpAsync(user.Id);
        var locked = await CreateUserAsync("locked@example.com");
        await EnrollTotpAsync(locked.Id);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failure = await LoginAsync("locked@example.com", WrongPassword);
            failure.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var wrongPassword = await LoginAsync(Email, WrongPassword);
        using var unknownEmail = await LoginAsync("nobody@example.com", WrongPassword);
        using var lockedWithTheRightPassword = await LoginAsync("locked@example.com");

        // Nothing two-step is looked at before the password is right and the account may sign in, so these are Plan 2's answers.
        await ProblemResponseComparison.AssertIdenticalAsync(
            HttpStatusCode.Unauthorized, "auth.invalid_credentials", wrongPassword, unknownEmail, lockedWithTheRightPassword);
        (await QueryAsync(context => context.Set<MfaChallenge>().CountAsync(Ct))).ShouldBe(0);

        // Only the right password of an account that may sign in gets the challenge (which says the password was right, as a 403 does).
        using var right = await LoginAsync(Email);
        right.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await right.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Wrong_codes_across_challenges_lock_the_account_after_five()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var wrongCode = WrongCode(enrollment);

        // Three wrong codes on the first challenge, then a new challenge (the password step resets nothing) and two more.
        var first = await ChallengeAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await VerifyAsync(first, code: wrongCode);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
        }

        var second = await ChallengeAsync();
        (await FindUserAsync(user.Id)).AccessFailedCount.ShouldBe(3);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await VerifyAsync(second, code: wrongCode);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
        }

        // The fifth wrong code locked the account: the right password gets the generic 401, the open challenge is dead even for the
        // right code, and that code is not spent.
        var stored = await FindUserAsync(user.Id);
        stored.AccessFailedCount.ShouldBe(5);
        stored.LockoutEnd.ShouldBe(Factory.Time.GetUtcNow() + LockoutDuration);
        using (var login = await LoginAsync(Email))
        {
            await AssertProblemAsync(login, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        using (var right = await VerifyAsync(second, code: enrollment.CodeAt(Factory.Time.GetUtcNow())))
        {
            await AssertProblemAsync(right, HttpStatusCode.BadRequest, "auth.invalid_mfa_challenge");
        }

        (await QueryAsync(context => context.Set<UserMfaMethod>().AsNoTracking().SingleAsync(Ct))).LastUsedTimeStep.ShouldBeNull();
        var audits = await AuditEventsAsync();
        audits.Count(type => type == AuthAuditEvents.MfaFailed).ShouldBe(6);
        audits.Count(type => type == AuthAuditEvents.LockedOut).ShouldBe(1);

        // The owner is told once.
        await DispatchNotificationsAsync();
        Factory.EmailSender.Sent.Where(message => message.To == Email).ShouldHaveSingleItem().Subject.ShouldBe("Your account is temporarily locked");

        // After the lockout, the password and a code sign in again, and the count starts over.
        Factory.Time.Advance(LockoutDuration);
        var third = await ChallengeAsync();
        using var verify = await VerifyAsync(third, code: enrollment.CodeAt(Factory.Time.GetUtcNow()));
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        stored = await FindUserAsync(user.Id);
        stored.AccessFailedCount.ShouldBe(0);
        stored.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public async Task Password_step_does_not_reset_the_failure_count_of_an_mfa_account()
    {
        var user = await CreateUserAsync(Email);
        await EnrollTotpAsync(user.Id);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var failure = await LoginAsync(Email, WrongPassword);
            failure.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        await ChallengeAsync();

        var stored = await FindUserAsync(user.Id);
        stored.AccessFailedCount.ShouldBe(2);
        stored.LastLoginAt.ShouldBeNull();
    }

    [Fact]
    public async Task Challenge_is_capped_at_five_wrong_codes()
    {
        await using var host = Factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:Lockout:MaxFailedAttempts", "20"));
        using var client = host.CreateClient();
        var user = await CreateUserAsync(Email);
        var enrollment = await AuthTestHarness.EnrollTotpAsync(host.Services, user.Id, Ct);
        var challenge = await ChallengeAsync(client);
        var wrongCode = WrongCode(enrollment);
        var rightCode = enrollment.CodeAt(Factory.Time.GetUtcNow());

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await VerifyAsync(challenge, code: wrongCode, client: client);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
        }

        // The sixth try is refused before the code is looked at, even the right one; the user's lockout (20 here) is not reached.
        using (var sixth = await VerifyAsync(challenge, code: rightCode, client: client))
        {
            await AssertProblemAsync(sixth, HttpStatusCode.BadRequest, "auth.invalid_mfa_challenge");
        }

        (await QueryAsync(context => context.Set<MfaChallenge>().AsNoTracking().SingleAsync(Ct))).FailedAttempts.ShouldBe(5);
        var stored = await FindUserAsync(user.Id);
        stored.AccessFailedCount.ShouldBe(5);
        stored.LockoutEnd.ShouldBeNull();

        // The right code was not spent by the refused try: a new challenge accepts it.
        var next = await ChallengeAsync(client);
        using var verify = await VerifyAsync(next, code: rightCode, client: client);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Concurrent_verifies_with_one_code_succeed_once()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var challenge = await ChallengeAsync();
        var code = enrollment.CodeAt(Factory.Time.GetUtcNow());

        // A double tap: both requests load the open challenge, only one claims the code's time step and the challenge.
        var responses = await Task.WhenAll(VerifyAsync(challenge, code: code), VerifyAsync(challenge, code: code));

        try
        {
            responses.Select(response => response.StatusCode).ShouldBe([HttpStatusCode.OK, HttpStatusCode.BadRequest], ignoreOrder: true);
            var refused = responses.Single(response => response.StatusCode == HttpStatusCode.BadRequest);
            (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString()
                .ShouldBeOneOf("auth.invalid_mfa_code", "auth.invalid_mfa_challenge");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        (await QueryAsync(context => context.Set<UserSession>().CountAsync(Ct))).ShouldBe(1);
        (await QueryAsync(context => context.Set<MfaChallenge>().AsNoTracking().SingleAsync(Ct))).ConsumedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Code_used_once_is_refused_on_a_second_challenge()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var code = enrollment.CodeAt(Factory.Time.GetUtcNow());
        using (var first = await VerifyAsync(await ChallengeAsync(), code: code))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var replay = await VerifyAsync(await ChallengeAsync(), code: code))
        {
            await AssertProblemAsync(replay, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
        }

        // A replayed code is a wrong code: it counts. The next time step's code works.
        (await FindUserAsync(user.Id)).AccessFailedCount.ShouldBe(1);
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        using var next = await VerifyAsync(await ChallengeAsync(), code: enrollment.CodeAt(Factory.Time.GetUtcNow()));
        next.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Older_code_after_a_newer_one_is_refused()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var now = Factory.Time.GetUtcNow();

        using (var newer = await VerifyAsync(await ChallengeAsync(), code: enrollment.CodeAt(now)))
        {
            newer.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // The previous step's code is still inside the clock tolerance, but a step at or below the last used one never counts again.
        using var older = await VerifyAsync(await ChallengeAsync(), code: enrollment.CodeAt(now.AddSeconds(-30)));

        await AssertProblemAsync(older, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
        (await QueryAsync(context => context.Set<UserMfaMethod>().AsNoTracking().SingleAsync(Ct))).LastUsedTimeStep.ShouldBe(Totp.TimeStepAt(now));
    }

    [Fact]
    public async Task Recovery_code_works_once_in_any_spelling()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var code = enrollment.RecoveryCodes[0];

        using var verify = await VerifyAsync(await ChallengeAsync(), recoveryCode: code.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant());

        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await verify.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var accessToken = tokens.GetProperty("accessToken").GetString()!;
        Amr(new JsonWebToken(accessToken)).ShouldBe(["pwd", "rcvr", "mfa"]);
        (await QueryAsync(context => context.Set<UserSession>().AsNoTracking().SingleAsync(Ct))).AuthMethods.ShouldBe("pwd rcvr mfa");
        (await QueryAsync(context => context.Set<OutboxMessage>().CountAsync(message => message.Type.Contains(nameof(MfaChangedDomainEvent)), Ct)))
            .ShouldBe(1);
        (await RemainingRecoveryCodesAsync(accessToken)).ShouldBe(9);

        // Single use, whatever the spelling; another code with spaces and a trailing newline works.
        using (var again = await VerifyAsync(await ChallengeAsync(), recoveryCode: code))
        {
            await AssertProblemAsync(again, HttpStatusCode.BadRequest, "auth.invalid_mfa_code");
        }

        using var spaced = await VerifyAsync(await ChallengeAsync(), recoveryCode: $" {enrollment.RecoveryCodes[1].Replace('-', ' ').ToLowerInvariant()}\n");
        spaced.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RemainingRecoveryCodesAsync(accessToken)).ShouldBe(8);
    }

    [Fact]
    public async Task Expired_challenge_is_invalid()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var early = await ChallengeAsync();
        var late = await ChallengeAsync();

        // One millisecond before the end it still works (the column keeps milliseconds); at the end it is invalid.
        Factory.Time.Advance(ChallengeLifetime - TimeSpan.FromMilliseconds(1));
        using (var inTime = await VerifyAsync(early, code: enrollment.CodeAt(Factory.Time.GetUtcNow())))
        {
            inTime.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // The refusal comes before the code is looked at, so the code (of the step just claimed) does not matter and nothing is counted.
        Factory.Time.Advance(TimeSpan.FromMilliseconds(1));
        using var expired = await VerifyAsync(late, code: enrollment.CodeAt(Factory.Time.GetUtcNow()));

        await AssertProblemAsync(expired, HttpStatusCode.BadRequest, "auth.invalid_mfa_challenge");
        (await FindUserAsync(user.Id)).AccessFailedCount.ShouldBe(0);
        (await QueryAsync(context => context.Set<MfaChallenge>().CountAsync(challenge => challenge.ConsumedAt == null && challenge.FailedAttempts == 0, Ct)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Used_challenge_is_invalid()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var challenge = await ChallengeAsync();
        using (var first = await VerifyAsync(challenge, code: enrollment.CodeAt(Factory.Time.GetUtcNow())))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        using var again = await VerifyAsync(challenge, code: enrollment.CodeAt(Factory.Time.GetUtcNow()));

        await AssertProblemAsync(again, HttpStatusCode.BadRequest, "auth.invalid_mfa_challenge");
        (await QueryAsync(context => context.Set<UserSession>().CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Challenge_after_a_password_change_is_invalid()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var challenge = await ChallengeAsync();

        // A password change (here straight through the domain) rotates the stamp the challenge is bound to.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var stored = await context.Set<User>().SingleAsync(candidate => candidate.Id == user.Id, Ct);
            stored.ChangePassword(hasher.Hash("a-brand-new-password-for-alice"), historyCount: 5, Factory.Time.GetUtcNow()).IsSuccess.ShouldBeTrue();
            await context.SaveChangesAsync(Ct);
        }

        using var response = await VerifyAsync(challenge, code: enrollment.CodeAt(Factory.Time.GetUtcNow()));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.invalid_mfa_challenge");
        (await FindUserAsync(user.Id)).AccessFailedCount.ShouldBe(0);
        (await QueryAsync(context => context.Set<UserMfaMethod>().AsNoTracking().SingleAsync(Ct))).LastUsedTimeStep.ShouldBeNull();
        (await QueryAsync(context => context.Set<UserSession>().CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_token_as_challenge_is_invalid_challenge()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        using var verify = await VerifyAsync(await ChallengeAsync(), code: enrollment.CodeAt(Factory.Time.GetUtcNow()));
        var refreshToken = (await verify.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("refreshToken").GetString()!;
        Factory.Time.Advance(TimeSpan.FromSeconds(30));

        using var response = await VerifyAsync(refreshToken, code: enrollment.CodeAt(Factory.Time.GetUtcNow()));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.invalid_mfa_challenge");
        (await FindUserAsync(user.Id)).AccessFailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Challenge_token_as_bearer_or_refresh_token_is_refused()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var challenge = await ChallengeAsync();

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, MeRoute).WithBearer(challenge);
        using var me = await Client.SendAsync(meRequest, Ct);
        using var refresh = await PostAsync(Client, RefreshRoute, new { refreshToken = challenge });

        await AssertProblemAsync(me, HttpStatusCode.Unauthorized, "http.401");
        await AssertProblemAsync(refresh, HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");

        // Neither touched the challenge: it still trades for tokens.
        using var verify = await VerifyAsync(challenge, code: enrollment.CodeAt(Factory.Time.GetUtcNow()));
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Verify_is_anonymous_rate_limited_and_no_store()
    {
        const int PermitLimit = 3;
        const string Address = "203.0.113.50";
        await using var limited = Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:AuthStrictPermitLimit", PermitLimit.ToString(CultureInfo.InvariantCulture)));
        using var client = limited.CreateClient();

        // Anonymous (no bearer token): an unknown challenge is the 400, never a 401, and the answer is not cached.
        for (var attempt = 0; attempt < PermitLimit; attempt++)
        {
            using var allowed = await VerifyAsync($"unknown-challenge-{attempt}", code: "123456", client: client, address: Address);
            await AssertProblemAsync(allowed, HttpStatusCode.BadRequest, "auth.invalid_mfa_challenge");
            AssertNoStore(allowed);
        }

        using var limitedResponse = await VerifyAsync("unknown-challenge", code: "123456", client: client, address: Address);
        using var otherAddress = await VerifyAsync("unknown-challenge", code: "123456", client: client, address: "203.0.113.51");

        await AssertProblemAsync(limitedResponse, HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
        limitedResponse.Headers.Contains("Retry-After").ShouldBeTrue();
        await AssertProblemAsync(otherAddress, HttpStatusCode.BadRequest, "auth.invalid_mfa_challenge");
    }

    [Fact]
    public async Task Request_without_exactly_one_proof_is_a_validation_problem()
    {
        using var neither = await VerifyAsync("some-challenge");
        using var both = await VerifyAsync("some-challenge", code: "123456", recoveryCode: "ABCD-EFGH-JKMN-PQRS");
        using var malformed = await VerifyAsync("some-challenge", code: "12345");
        using var blankToken = await VerifyAsync(" ", code: "123456");

        foreach (var (response, field) in new[] { (neither, "proof"), (both, "proof"), (malformed, "code"), (blankToken, "challengeToken") })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("code").GetString().ShouldBe("validation.failed");
            body.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(field);
        }

        (await QueryAsync(context => context.Set<AuthAuditLog>().CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Audit_and_metrics_never_contain_codes()
    {
        var logs = new RecordingLoggerProvider();
        await using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
        }));
        using var client = host.CreateClient();
        using var metrics = new AuthMeasurements();
        var user = await CreateUserAsync(Email);
        var enrollment = await AuthTestHarness.EnrollTotpAsync(host.Services, user.Id, Ct);
        var wrongCode = WrongCode(enrollment);
        var rightCode = enrollment.CodeAt(Factory.Time.GetUtcNow());
        const string WrongRecoveryCode = "2345-6789-ABCD-EFGH";
        var typedRecoveryCode = enrollment.RecoveryCodes[0].Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        // Every outcome once: a wrong code, a wrong recovery code, a right code, a right recovery code.
        var first = await ChallengeAsync(client);
        using (var response = await VerifyAsync(first, code: wrongCode, client: client))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var response = await VerifyAsync(first, recoveryCode: WrongRecoveryCode, client: client))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var response = await VerifyAsync(first, code: rightCode, client: client))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var second = await ChallengeAsync(client);
        using (var response = await VerifyAsync(second, recoveryCode: typedRecoveryCode, client: client))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        string[] secrets =
        [
            first, second, wrongCode, rightCode, WrongRecoveryCode, WrongRecoveryCode.Replace("-", string.Empty, StringComparison.Ordinal), typedRecoveryCode,
            .. enrollment.RecoveryCodes, .. enrollment.RecoveryCodes.Select(code => RecoveryCodeFormat.Normalize(code)!),
        ];

        // The audit rows of every outcome exist and hold none of them.
        var audits = await QueryAsync(context => context.Set<AuthAuditLog>().AsNoTracking().OrderBy(entry => entry.Id).ToListAsync(Ct));
        audits.Select(entry => entry.EventType).ShouldContain(AuthAuditEvents.MfaFailed);
        audits.Select(entry => entry.EventType).ShouldContain(AuthAuditEvents.MfaVerified);
        foreach (var entry in audits)
        {
            string?[] texts = [entry.EventType, entry.FailureReason, entry.AttemptedIdentifier, entry.Details, entry.UserAgent, entry.IpAddress, entry.TraceId];
            texts.ShouldAllBe(text => text == null || !ContainsAny(text, secrets));
        }

        // The counters carry fixed tag values only.
        metrics.Tags("auth.mfa_verifications").ShouldBe(
            ["result=invalid_code factor=totp", "result=invalid_code factor=recovery_code", "result=succeeded factor=totp", "result=succeeded factor=recovery_code"]);
        metrics.Tags("auth.logins").ShouldBe(["result=mfa_required", "result=mfa_required"]);
        metrics.All.ShouldAllBe(tags => !ContainsAny(tags, secrets));

        // Every log line of every level and category: the handler ran and was logged, and no line holds a code, a recovery code or a token.
        logs.Entries.ShouldContain(entry => entry.Contains("Completed VerifyMfaCommand", StringComparison.Ordinal));
        logs.Entries.ShouldContain(entry => entry.Contains("Failed VerifyMfaCommand with auth.invalid_mfa_code", StringComparison.Ordinal));
        logs.Entries.ShouldAllBe(entry => !ContainsAny(entry, secrets));
    }

    private static List<string> PropertyNames(JsonElement body) => [.. body.EnumerateObject().Select(property => property.Name)];

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
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(code);
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds one of <paramref name="secrets"/> as a whole word (not inside a longer run of letters and
    /// digits), ignoring case: a 6-digit code must not be found inside a GUID or a trace id by chance.
    /// </summary>
    private static bool ContainsAny(string text, IEnumerable<string> secrets)
        => secrets.Any(secret => Regex.IsMatch(text, $"(?<![0-9A-Za-z]){Regex.Escape(secret)}(?![0-9A-Za-z])", RegexOptions.IgnoreCase));

    /// <summary>A 6-digit code the authenticator would not show now (none of the three steps the verifier accepts).</summary>
    private string WrongCode(TotpEnrollment enrollment)
    {
        var now = Factory.Time.GetUtcNow();
        var accepted = new[] { -30, 0, 30 }.Select(seconds => enrollment.CodeAt(now.AddSeconds(seconds))).ToHashSet(StringComparer.Ordinal);
        return new[] { "000000", "111111", "222222", "333333" }.First(code => !accepted.Contains(code));
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password = AuthTestHarness.Password, string? deviceName = null, HttpClient? client = null)
        => PostAsync(client ?? Client, LoginRoute, new { email, password, deviceName });

    /// <summary>Signs <see cref="Email"/> in with the right password and returns the challenge token of the answer.</summary>
    private async Task<string> ChallengeAsync(HttpClient? client = null)
    {
        using var response = await LoginAsync(Email, client: client);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        return body.GetProperty("challengeToken").GetString()!;
    }

    private Task<HttpResponseMessage> VerifyAsync(
        string challengeToken,
        string? code = null,
        string? recoveryCode = null,
        HttpClient? client = null,
        string? address = null)
        => PostAsync(client ?? Client, VerifyRoute, new { challengeToken, code, recoveryCode }, address);

    /// <summary>Posts <paramref name="body"/> anonymously from <paramref name="address"/>, or from an address no request used before.</summary>
    private async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object body, string? address = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, address ?? $"198.51.100.{Interlocked.Increment(ref _addressCount)}");
        return await client.SendAsync(request, Ct);
    }

    private async Task<int> RemainingRecoveryCodesAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MethodsRoute).WithBearer(accessToken);
        using var response = await Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("recoveryCodesRemaining").GetInt32();
    }

    private Task<User> FindUserAsync(Guid userId)
        => QueryAsync(context => context.Set<User>().AsNoTracking().SingleAsync(user => user.Id == userId, Ct));

    private Task<List<string>> AuditEventsAsync()
        => QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).Select(entry => entry.EventType).ToListAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    /// <summary>Records every measurement of the Auth meter, of every host, while it lives: the instrument and its tags as text.</summary>
    private sealed class AuthMeasurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Lock _lock = new();
        private readonly List<(string Instrument, string Tags)> _measurements = [];

        public AuthMeasurements()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AuthMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                var text = string.Join(" ", tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}"));
                lock (_lock)
                {
                    _measurements.Add((instrument.Name, text));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<string> All
        {
            get
            {
                lock (_lock)
                {
                    return [.. _measurements.Select(measurement => $"{measurement.Instrument} {measurement.Tags}")];
                }
            }
        }

        public IReadOnlyList<string> Tags(string instrument)
        {
            lock (_lock)
            {
                return [.. _measurements.Where(measurement => measurement.Instrument == instrument).Select(measurement => measurement.Tags)];
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
