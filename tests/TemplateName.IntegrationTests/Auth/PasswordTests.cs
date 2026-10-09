using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// <c>POST /api/v1/auth/password/forgot</c>, <c>/reset</c> and <c>/change</c> through the running host. The outbox does not run by
/// itself in tests, so the tests dispatch it and read the reset links <see cref="RecordingEmailSender"/> kept.
/// </summary>
public sealed class PasswordTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string ForgotRoute = "/api/v1/auth/password/forgot";
    private const string ResetRoute = "/api/v1/auth/password/reset";
    private const string ChangeRoute = "/api/v1/auth/password/change";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string RegisterRoute = "/api/v1/auth/register";
    private const string Email = "alice@example.com";
    private const string OldPassword = AuthTestHarness.Password;
    private const string NewPassword = "a brand new passphrase";
    private const string ResetSubject = "Reset your password";

    private static readonly string[] VolatileHeaders = ["X-Trace-Id", "Date"];

    [Fact]
    public async Task Forgot_returns_identical_202_for_known_and_unknown_email()
    {
        await CreateUserAsync(Email);

        using var forKnownEmail = await ForgotAsync(Email);
        using var forUnknownEmail = await ForgotAsync("nobody@example.com");

        forKnownEmail.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        forUnknownEmail.StatusCode.ShouldBe(forKnownEmail.StatusCode);
        Headers(forUnknownEmail).ShouldBe(Headers(forKnownEmail));
        (await forKnownEmail.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        (await forUnknownEmail.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();

        // Both are audited with the masked address only; the unknown one has no user and issued no code.
        var audits = await QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).ToListAsync(Ct));
        audits.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.PasswordForgotRequested, AuthAuditEvents.PasswordForgotRequested]);
        audits[0].Succeeded.ShouldBeTrue();
        audits[0].AttemptedIdentifier.ShouldBe("a****@example.com");
        audits[1].Succeeded.ShouldBeFalse();
        audits[1].UserId.ShouldBeNull();
        audits[1].AttemptedIdentifier.ShouldBe("n****@example.com");
        (await QueryAsync(context => context.Set<VerificationCode>().CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Forgot_sends_a_reset_email_after_outbox_dispatch()
    {
        await CreateUserAsync(Email);

        using (var response = await ForgotAsync(" Alice@Example.COM "))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        Factory.EmailSender.Sent.ShouldBeEmpty();
        await DispatchOutboxAsync();

        var email = Factory.EmailSender.Sent.ShouldHaveSingleItem();
        email.To.ShouldBe(Email);
        email.Subject.ShouldBe(ResetSubject);
        email.TextBody.ShouldContain("http://localhost:3000/reset-password?token=");
        var token = Factory.EmailSender.LastLinkToken(Email);
        var code = await QueryAsync(context => context.Set<VerificationCode>().SingleAsync(Ct));
        code.Purpose.ShouldBe(VerificationPurpose.PasswordReset);
        code.TokenHash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        code.ExpiresAt.ShouldBe(Factory.Time.GetUtcNow().AddMinutes(30));
        var contents = await QueryAsync(context => context.Set<OutboxMessage>().Select(message => message.Content).ToListAsync(Ct));
        contents.ShouldAllBe(content => !content.Contains(token) && !content.Contains(Uri.EscapeDataString(token)));
    }

    [Fact]
    public async Task Reset_with_valid_token_changes_the_password_and_old_password_fails()
    {
        var user = await CreateUserAsync(Email);
        var token = await ForgotAndReadTokenAsync(Email);

        using (var reset = await ResetAsync(token, NewPassword))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await reset.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        }

        using (var withNew = await LoginAsync(Email, NewPassword))
        {
            withNew.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var withOld = await LoginAsync(Email, OldPassword))
        {
            await AssertProblemAsync(withOld, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        var stored = await FindUserAsync(Email);
        stored.SecurityStamp.ShouldNotBe(user.SecurityStamp);
        stored.PasswordHistory.Count.ShouldBe(2);
        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.PasswordReset, Ct));
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Reset_token_is_single_use()
    {
        await CreateUserAsync(Email);
        var token = await ForgotAndReadTokenAsync(Email);
        using (var first = await ResetAsync(token, NewPassword))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var second = await ResetAsync(token, "yet another passphrase");

        await AssertProblemAsync(second, HttpStatusCode.BadRequest, "auth.invalid_token");
        using var withNew = await LoginAsync(Email, NewPassword);
        withNew.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuditEventsAsync()).Count(type => type == AuthAuditEvents.PasswordResetFailed).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_reset_with_same_token_succeeds_once()
    {
        await CreateUserAsync(Email);
        var token = await ForgotAndReadTokenAsync(Email);

        var responses = await Task.WhenAll(ResetAsync(token, NewPassword), ResetAsync(token, "the other new passphrase"));

        try
        {
            // The code is consumed by one conditional UPDATE: exactly one request changes the password, the other gets the ordinary
            // invalid-token answer (never a 409 or a 500).
            responses.Select(response => (int)response.StatusCode).Order().ShouldBe([204, 400]);
            await AssertProblemAsync(responses.Single(response => response.StatusCode == HttpStatusCode.BadRequest), HttpStatusCode.BadRequest, "auth.invalid_token");
            var events = await AuditEventsAsync();
            events.Count(type => type == AuthAuditEvents.PasswordReset).ShouldBe(1);
            events.Count(type => type == AuthAuditEvents.PasswordResetFailed).ShouldBe(1);
            (await FindUserAsync(Email)).PasswordHistory.Count.ShouldBe(2);
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
    public async Task Reset_to_a_recent_password_uses_up_the_link()
    {
        // The reuse check runs after the code is consumed: a link cannot be used to test candidate passwords for free.
        await CreateUserAsync(Email);
        var token = await ForgotAndReadTokenAsync(Email);

        using (var reused = await ResetAsync(token, OldPassword))
        {
            await AssertProblemAsync(reused, HttpStatusCode.BadRequest, "auth.password_reused");
        }

        using (var retry = await ResetAsync(token, NewPassword))
        {
            await AssertProblemAsync(retry, HttpStatusCode.BadRequest, "auth.invalid_token");
        }

        (await QueryAsync(context => context.Set<VerificationCode>().SingleAsync(Ct))).ConsumedAt.ShouldNotBeNull();
        using var withOld = await LoginAsync(Email, OldPassword);
        withOld.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reset_with_expired_token_fails()
    {
        await CreateUserAsync(Email);
        var token = await ForgotAndReadTokenAsync(Email);
        Factory.Time.Advance(TimeSpan.FromMinutes(30));

        using var response = await ResetAsync(token, NewPassword);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.invalid_token");
        using var withOld = await LoginAsync(Email, OldPassword);
        withOld.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Token_of_another_purpose_is_rejected()
    {
        // Review Focus 3: the email-confirmation token from registration, presented to the reset endpoint.
        using (var register = await Client.PostAsJsonAsync(RegisterRoute, new { email = Email, password = OldPassword, displayName = "Alice", locale = "en" }, Ct))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await DispatchOutboxAsync();
        var confirmToken = Factory.EmailSender.LastLinkToken(Email);

        using var response = await ResetAsync(confirmToken, NewPassword);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.invalid_token");
        var user = await FindUserAsync(Email);
        user.EmailConfirmed.ShouldBeFalse();
        user.PasswordHistory.Count.ShouldBe(1);
        var code = await QueryAsync(context => context.Set<VerificationCode>().SingleAsync(Ct));
        code.Purpose.ShouldBe(VerificationPurpose.EmailVerify);
        code.ConsumedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Superseded_token_is_rejected()
    {
        await CreateUserAsync(Email);
        var first = await ForgotAndReadTokenAsync(Email);
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await ForgotAndReadTokenAsync(Email);
        second.ShouldNotBe(first);

        using (var withFirst = await ResetAsync(first, NewPassword))
        {
            await AssertProblemAsync(withFirst, HttpStatusCode.BadRequest, "auth.invalid_token");
        }

        using var withSecond = await ResetAsync(second, NewPassword);
        withSecond.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reset_kills_every_other_pending_reset_link()
    {
        // Two forgots at the same moment can leave two live links; a reset through one of them ends the other.
        var user = await CreateUserAsync(Email);
        var first = await ForgotAndReadTokenAsync(Email);
        var second = await IssuePendingResetLinkAsync(user.Id);

        using (var reset = await ResetAsync(first, NewPassword))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var withSecond = await ResetAsync(second, "yet another passphrase");
        await AssertProblemAsync(withSecond, HttpStatusCode.BadRequest, "auth.invalid_token");
        using var withNew = await LoginAsync(Email, NewPassword);
        withNew.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Change_kills_pending_reset_links()
    {
        // A link emailed before the change (or leaked) must not outlive the password it was meant to replace.
        await CreateUserAsync(Email);
        var link = await ForgotAndReadTokenAsync(Email);
        var signedIn = await ReadTokensAsync(await LoginAsync(Email, OldPassword));

        using (var change = await ChangeAsync(signedIn.AccessToken, OldPassword, NewPassword))
        {
            change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var reset = await ResetAsync(link, "yet another passphrase");
        await AssertProblemAsync(reset, HttpStatusCode.BadRequest, "auth.invalid_token");
        using var withNew = await LoginAsync(Email, NewPassword);
        withNew.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reset_revokes_all_refresh_tokens()
    {
        await CreateUserAsync(Email);
        var phone = await ReadTokensAsync(await LoginAsync(Email, OldPassword));
        var laptop = await ReadTokensAsync(await LoginAsync(Email, OldPassword));
        var token = await ForgotAndReadTokenAsync(Email);

        using (var reset = await ResetAsync(token, NewPassword))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        foreach (var signedIn in new[] { phone, laptop })
        {
            using var refresh = await RefreshAsync(signedIn.RefreshToken);
            await AssertProblemAsync(refresh, HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
            var session = await QueryAsync(context => context.Set<UserSession>().SingleAsync(candidate => candidate.Id == signedIn.SessionId, Ct));
            session.RevokedReason.ShouldBe(SessionRevokedReason.PasswordChanged);
        }
    }

    [Fact]
    public async Task Reset_clears_lockout_and_confirms_email()
    {
        await CreateUserAsync(Email, confirmed: false);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await LoginAsync(Email, "not the password at all");
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await FindUserAsync(Email)).LockoutEnd.ShouldNotBeNull();
        var token = await ForgotAndReadTokenAsync(Email);

        using (var reset = await ResetAsync(token, NewPassword))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var user = await FindUserAsync(Email);
        user.EmailConfirmed.ShouldBeTrue();
        user.LockoutEnd.ShouldBeNull();
        user.AccessFailedCount.ShouldBe(0);
        using var login = await LoginAsync(Email, NewPassword);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Account_created_without_a_password_sets_its_first_one_through_reset()
    {
        // Ruling R3: such an account is unknown to login but known to forgot, which is how it gets its first password.
        await CreateUserAsync(Email, password: null, confirmed: false);
        var token = await ForgotAndReadTokenAsync(Email);

        using (var reset = await ResetAsync(token, NewPassword))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var login = await LoginAsync(Email, NewPassword);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reset_for_a_suspended_account_fails_as_invalid_token_and_keeps_it_suspended()
    {
        var user = await CreateUserAsync(Email);
        var token = await ForgotAndReadTokenAsync(Email);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var stored = await context.Set<User>().SingleAsync(candidate => candidate.Id == user.Id, Ct);
            stored.Suspend(Factory.Time.GetUtcNow());
            await context.SaveChangesAsync(Ct);
        }

        using var response = await ResetAsync(token, NewPassword);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.invalid_token");
        var after = await FindUserAsync(Email);
        after.Status.ShouldBe(UserStatus.Suspended);
        after.PasswordHistory.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Forgot_within_cooldown_sends_one_email()
    {
        await CreateUserAsync(Email);

        await AssertForgotAcceptedAsync(Email);
        Factory.Time.Advance(TimeSpan.FromSeconds(59));
        await AssertForgotAcceptedAsync(Email);
        await DispatchOutboxAsync();
        Factory.EmailSender.Sent.ShouldHaveSingleItem().Subject.ShouldBe(ResetSubject);

        // The cooldown is 60 seconds from the last code: exactly then, a new one goes out.
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        await AssertForgotAcceptedAsync(Email);
        await DispatchOutboxAsync();
        Factory.EmailSender.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Change_password_kills_other_sessions_but_keeps_the_current_one()
    {
        // Spec 12.2: the session that changed the password stays signed in; every other one ends.
        await CreateUserAsync(Email);
        var current = await ReadTokensAsync(await LoginAsync(Email, OldPassword));
        var other = await ReadTokensAsync(await LoginAsync(Email, OldPassword));

        using (var change = await ChangeAsync(current.AccessToken, OldPassword, NewPassword))
        {
            change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var keptRefresh = await RefreshAsync(current.RefreshToken))
        {
            keptRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var otherRefresh = await RefreshAsync(other.RefreshToken))
        {
            await AssertProblemAsync(otherRefresh, HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        }

        var sessions = await QueryAsync(context => context.Set<UserSession>().ToListAsync(Ct));
        sessions.Single(session => session.Id == current.SessionId).RevokedAt.ShouldBeNull();
        sessions.Single(session => session.Id == other.SessionId).RevokedReason.ShouldBe(SessionRevokedReason.PasswordChanged);
        using (var withNew = await LoginAsync(Email, NewPassword))
        {
            withNew.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var withOld = await LoginAsync(Email, OldPassword))
        {
            withOld.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var audit = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.PasswordChanged, Ct));
        audit.SessionId.ShouldBe(current.SessionId);
    }

    [Fact]
    public async Task Change_with_wrong_current_password_returns_400()
    {
        await CreateUserAsync(Email);
        var signedIn = await ReadTokensAsync(await LoginAsync(Email, OldPassword));

        using var response = await ChangeAsync(signedIn.AccessToken, "not the current password", NewPassword);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "auth.current_password_incorrect");
        var user = await FindUserAsync(Email);
        user.PasswordHistory.Count.ShouldBe(1);
        user.AccessFailedCount.ShouldBe(0);
        using (var refresh = await RefreshAsync(signedIn.RefreshToken))
        {
            refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var failed = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.PasswordChangeFailed, Ct));
        failed.FailureReason.ShouldBe("auth.current_password_incorrect");
        failed.SessionId.ShouldBe(signedIn.SessionId);
    }

    [Fact]
    public async Task Change_to_a_recent_password_returns_auth_password_reused()
    {
        await CreateUserAsync(Email);
        var signedIn = await ReadTokensAsync(await LoginAsync(Email, OldPassword));

        using (var same = await ChangeAsync(signedIn.AccessToken, OldPassword, OldPassword))
        {
            await AssertProblemAsync(same, HttpStatusCode.BadRequest, "auth.password_reused");
        }

        using (var change = await ChangeAsync(signedIn.AccessToken, OldPassword, NewPassword))
        {
            change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The previous password is still in the history, so going back to it is refused too.
        using var back = await ChangeAsync(signedIn.AccessToken, NewPassword, OldPassword);
        await AssertProblemAsync(back, HttpStatusCode.BadRequest, "auth.password_reused");
    }

    [Fact]
    public async Task Change_without_token_is_401()
    {
        using var response = await Client.PostAsJsonAsync(ChangeRoute, new { currentPassword = OldPassword, newPassword = NewPassword }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("http.401");
    }

    [Theory]
    [InlineData(129)]
    [InlineData(1024 * 1024)]
    public async Task Oversized_new_password_is_rejected_with_400(int length)
    {
        await CreateUserAsync(Email);
        var signedIn = await ReadTokensAsync(await LoginAsync(Email, OldPassword));
        var token = await ForgotAndReadTokenAsync(Email);
        var oversized = new string('p', length);

        using var reset = await ResetAsync(token, oversized);
        using var change = await ChangeAsync(signedIn.AccessToken, OldPassword, oversized);

        foreach (var response in new[] { reset, change })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("errors").TryGetProperty("newPassword", out _).ShouldBeTrue();
            body.GetRawText().ShouldNotContain("pppppppppppp");
        }

        (await FindUserAsync(Email)).PasswordHistory.Count.ShouldBe(1);
        var code = await QueryAsync(context => context.Set<VerificationCode>().SingleAsync(Ct));
        code.ConsumedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(ForgotRoute)]
    [InlineData(ResetRoute)]
    [InlineData(ChangeRoute)]
    public async Task Rate_limit_returns_429_with_retry_after(string route)
    {
        const int PermitLimit = 3;
        await SignInAsync();
        await using var limited = Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:AuthStrictPermitLimit", PermitLimit.ToString(CultureInfo.InvariantCulture)));
        using var client = await CreateClientAsync(limited);

        for (var attempt = 0; attempt < PermitLimit; attempt++)
        {
            using var allowed = await client.PostAsJsonAsync(route, Body(route, attempt), Ct);
            allowed.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        using var response = await client.PostAsJsonAsync(route, Body(route, PermitLimit), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.Contains("Retry-After").ShouldBeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("rate_limit.exceeded");
    }

    private static object Body(string route, int attempt) => route switch
    {
        ForgotRoute => new { email = $"user{attempt}@example.com" },
        ResetRoute => new { token = $"unknown-token-{attempt}", newPassword = NewPassword },
        _ => new { currentPassword = $"wrong password {attempt}", newPassword = NewPassword },
    };

    private static Dictionary<string, string> Headers(HttpResponseMessage response)
        => response.Headers.Concat(response.Content.Headers)
            .Where(header => !VolatileHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe(code);
        body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
    }

    private static async Task<Tokens> ReadTokensAsync(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!, body.GetProperty("sessionId").GetGuid());
        }
    }

    private async Task AssertForgotAcceptedAsync(string email)
    {
        using var response = await ForgotAsync(email);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    private Task<HttpResponseMessage> ForgotAsync(string email) => Client.PostAsJsonAsync(ForgotRoute, new { email }, Ct);

    private Task<HttpResponseMessage> ResetAsync(string token, string newPassword) => Client.PostAsJsonAsync(ResetRoute, new { token, newPassword }, Ct);

    private Task<HttpResponseMessage> LoginAsync(string email, string password) => Client.PostAsJsonAsync(LoginRoute, new { email, password }, Ct);

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) => Client.PostAsJsonAsync(RefreshRoute, new { refreshToken }, Ct);

    private async Task<HttpResponseMessage> ChangeAsync(string accessToken, string currentPassword, string newPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ChangeRoute)
        {
            Content = JsonContent.Create(new { currentPassword, newPassword }),
        }.WithBearer(accessToken);
        return await Client.SendAsync(request, Ct);
    }

    /// <summary>Asks for a reset link, delivers the email and returns the token from the link.</summary>
    private async Task<string> ForgotAndReadTokenAsync(string email)
    {
        await AssertForgotAcceptedAsync(email);
        await DispatchOutboxAsync();
        return Factory.EmailSender.LastLinkToken(email);
    }

    /// <summary>A second pending reset link next to the one forgot issued, as two simultaneous forgots can leave.</summary>
    private async Task<string> IssuePendingResetLinkAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var token = scope.ServiceProvider.GetRequiredService<ISecureTokenService>().Generate();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        context.Add(VerificationCode.Issue(
            userId,
            VerificationPurpose.PasswordReset,
            User.NormalizeEmail(Email),
            token.Hash,
            scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect(token.Value),
            TimeSpan.FromMinutes(30),
            null,
            Factory.Time.GetUtcNow()));
        await context.SaveChangesAsync(Ct);
        return token.Value;
    }

    private async Task DispatchOutboxAsync()
    {
        var dispatcher = Factory.Services.GetRequiredService<OutboxDispatcher<AuthDbContext>>();
        int claimed;
        do
        {
            claimed = await dispatcher.ProcessBatchAsync(Ct);
        }
        while (claimed > 0);
    }

    private Task<User> FindUserAsync(string email)
        => QueryAsync(context => context.Set<User>()
            .Include(user => user.PasswordHistory)
            .SingleAsync(user => user.NormalizedEmail == User.NormalizeEmail(email), Ct));

    private Task<List<string>> AuditEventsAsync()
        => QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).Select(entry => entry.EventType).ToListAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);
}
