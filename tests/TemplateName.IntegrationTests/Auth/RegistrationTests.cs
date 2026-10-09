using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// Registration, email confirmation and resending the confirmation through the running host. The outbox does not run by itself in tests,
/// so every test dispatches the Auth outbox after a call and reads the emails <see cref="RecordingEmailSender"/> kept.
/// </summary>
public sealed class RegistrationTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string RegisterRoute = "/api/v1/auth/register";
    private const string ConfirmRoute = "/api/v1/auth/email/confirm";
    private const string ResendRoute = "/api/v1/auth/email/resend-confirmation";
    private const string Password = "correct horse battery staple";
    private const string ConfirmSubject = "Confirm your email address";
    private const string AttemptSubject = "Sign-up attempt on your account";

    private static readonly string[] VolatileHeaders = ["X-Trace-Id", "Date"];

    [Fact]
    public async Task Register_sends_one_confirmation_email_and_confirm_returns_204()
    {
        using var response = await RegisterAsync("alice@example.com");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        await DispatchOutboxAsync();
        var email = Factory.EmailSender.Sent.ShouldHaveSingleItem();
        email.To.ShouldBe("alice@example.com");
        email.Subject.ShouldBe(ConfirmSubject);
        email.TextBody.ShouldContain("http://localhost:3000/confirm-email?token=");
        var token = Factory.EmailSender.LastLinkToken("alice@example.com");
        (await FindUserAsync("alice@example.com")).EmailConfirmed.ShouldBeFalse();

        using var confirm = await ConfirmAsync(token);

        confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await FindUserAsync("alice@example.com")).EmailConfirmed.ShouldBeTrue();
        (await AuditEventsAsync()).ShouldBe([AuthAuditEvents.Registered, AuthAuditEvents.EmailConfirmed]);
    }

    [Fact]
    public async Task Confirm_token_is_single_use()
    {
        var token = await RegisterAndReadTokenAsync("alice@example.com");
        using (var first = await ConfirmAsync(token))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var second = await ConfirmAsync(token);

        await AssertInvalidTokenAsync(second);
        (await AuditEventsAsync()).ShouldBe([AuthAuditEvents.Registered, AuthAuditEvents.EmailConfirmed, AuthAuditEvents.EmailConfirmFailed]);
    }

    [Fact]
    public async Task Concurrent_confirm_with_same_token_succeeds_once()
    {
        var token = await RegisterAndReadTokenAsync("alice@example.com");

        var responses = await Task.WhenAll(ConfirmAsync(token), ConfirmAsync(token));

        try
        {
            // The code is consumed by one conditional UPDATE, so the loser gets the ordinary invalid-token answer, never a 409 or 500.
            responses.Select(response => (int)response.StatusCode).Order().ShouldBe([204, 400]);
            await AssertInvalidTokenAsync(responses.Single(response => response.StatusCode == HttpStatusCode.BadRequest));
            (await FindUserAsync("alice@example.com")).EmailConfirmed.ShouldBeTrue();
            var events = await AuditEventsAsync();
            events.Count(type => type == AuthAuditEvents.EmailConfirmed).ShouldBe(1);
            events.Count(type => type == AuthAuditEvents.EmailConfirmFailed).ShouldBe(1);
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
    public async Task Confirm_with_expired_token_fails()
    {
        var token = await RegisterAndReadTokenAsync("alice@example.com");
        Factory.Time.Advance(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(1));

        using var response = await ConfirmAsync(token);

        await AssertInvalidTokenAsync(response);
        (await FindUserAsync("alice@example.com")).EmailConfirmed.ShouldBeFalse();
    }

    [Fact]
    public async Task Confirm_just_before_expiry_succeeds()
    {
        var token = await RegisterAndReadTokenAsync("alice@example.com");
        Factory.Time.Advance(TimeSpan.FromMinutes(60) - TimeSpan.FromMilliseconds(1));

        using var response = await ConfirmAsync(token);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Token_of_another_purpose_is_rejected()
    {
        await RegisterAndReadTokenAsync("alice@example.com");
        var user = await FindUserAsync("alice@example.com");
        string resetToken;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var generated = scope.ServiceProvider.GetRequiredService<ISecureTokenService>().Generate();
            resetToken = generated.Value;
            var code = VerificationCode.Issue(
                user.Id,
                VerificationPurpose.PasswordReset,
                VerificationTrigger.SelfService,
                user.NormalizedEmail,
                generated.Hash,
                "not-used-by-this-test",
                TimeSpan.FromMinutes(30),
                createdIp: null,
                Factory.Time.GetUtcNow());
            scope.ServiceProvider.GetRequiredService<IVerificationCodeRepository>().Add(code);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var response = await ConfirmAsync(resetToken);

        await AssertInvalidTokenAsync(response);
        (await FindUserAsync("alice@example.com")).EmailConfirmed.ShouldBeFalse();
        var resetCode = await QueryAsync(context => context.Set<VerificationCode>().SingleAsync(code => code.Purpose == VerificationPurpose.PasswordReset, Ct));
        resetCode.ConsumedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Superseded_token_is_rejected()
    {
        var first = await RegisterAndReadTokenAsync("alice@example.com");
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        using (var resend = await ResendAsync("alice@example.com"))
        {
            resend.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await DispatchOutboxAsync();
        Factory.EmailSender.Sent.Count.ShouldBe(2);
        var second = Factory.EmailSender.LastLinkToken("alice@example.com");
        second.ShouldNotBe(first);

        using (var withFirst = await ConfirmAsync(first))
        {
            await AssertInvalidTokenAsync(withFirst);
        }

        using var withSecond = await ConfirmAsync(second);
        withSecond.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AuditEventsAsync()).ShouldBe(
            [AuthAuditEvents.Registered, AuthAuditEvents.ConfirmationResent, AuthAuditEvents.EmailConfirmFailed, AuthAuditEvents.EmailConfirmed]);
    }

    [Fact]
    public async Task Register_existing_email_returns_the_same_202_and_sends_the_attempt_notice()
    {
        await RegisterAndReadTokenAsync("alice@example.com");
        var original = await FindUserAsync("alice@example.com");
        Factory.EmailSender.Clear();

        using var response = await RegisterAsync("alice@example.com", password: "another long password", displayName: "Mallory");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        await DispatchOutboxAsync();
        var notice = Factory.EmailSender.Sent.ShouldHaveSingleItem();
        notice.To.ShouldBe("alice@example.com");
        notice.Subject.ShouldBe(AttemptSubject);
        notice.TextBody.ShouldNotContain("token=");
        notice.TextBody.ShouldContain("Hello Alice,");

        var unchanged = await FindUserAsync("alice@example.com");
        unchanged.PasswordHash.ShouldBe(original.PasswordHash);
        unchanged.DisplayName.ShouldBe("Alice");
        (await QueryAsync(context => context.Set<User>().CountAsync(Ct))).ShouldBe(1);
        (await QueryAsync(context => context.Set<VerificationCode>().CountAsync(Ct))).ShouldBe(1);
        var duplicate = await QueryAsync(context => context.Set<AuthAuditLog>().SingleAsync(entry => entry.EventType == AuthAuditEvents.RegisterDuplicate, Ct));
        duplicate.UserId.ShouldBe(original.Id);
        duplicate.Succeeded.ShouldBeFalse();
        duplicate.AttemptedIdentifier.ShouldBe("a****@example.com");
    }

    [Fact]
    public async Task Concurrent_registrations_of_one_new_address_create_one_account_and_never_a_500()
    {
        // The requests pass the lookup at about the same moment; the unique email index lets one insert win, and every other one
        // answers exactly as for a known address (202, and the attempt is recorded), never a 500.
        var responses = await Task.WhenAll(
            Enumerable.Range(1, 6).Select(index => RegisterFromAsync("alice@example.com", $"198.51.100.{index}")));

        try
        {
            responses.Select(response => response.StatusCode).ShouldAllBe(status => status == HttpStatusCode.Accepted);
            foreach (var response in responses)
            {
                (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
            }

            (await QueryAsync(context => context.Set<User>().CountAsync(user => user.NormalizedEmail == "ALICE@EXAMPLE.COM", Ct))).ShouldBe(1);
            var events = await AuditEventsAsync();
            events.Count(type => type == AuthAuditEvents.Registered).ShouldBe(1);
            events.Count(type => type == AuthAuditEvents.RegisterDuplicate).ShouldBe(5);
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
    public async Task Register_response_is_identical_for_new_and_existing_email()
    {
        using var forNewEmail = await RegisterAsync("alice@example.com");
        using var forExistingEmail = await RegisterAsync("alice@example.com", displayName: "Mallory");

        forExistingEmail.StatusCode.ShouldBe(forNewEmail.StatusCode);
        forNewEmail.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Headers(forExistingEmail).ShouldBe(Headers(forNewEmail));
        (await forNewEmail.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        (await forExistingEmail.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Email_is_case_and_whitespace_insensitive()
    {
        using (var first = await RegisterAsync("Alice@Example.com "))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        using (var second = await RegisterAsync("alice@example.com"))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await DispatchOutboxAsync();
        var user = (await QueryAsync(context => context.Set<User>().ToListAsync(Ct))).ShouldHaveSingleItem();
        user.Email.ShouldBe("Alice@Example.com");
        user.NormalizedEmail.ShouldBe("ALICE@EXAMPLE.COM");

        // Both events were saved at the same instant and the outbox does not order messages (ADR 0007).
        Factory.EmailSender.Sent.Select(message => message.Subject).ShouldBe([ConfirmSubject, AttemptSubject], ignoreOrder: true);
        Factory.EmailSender.Sent.ShouldAllBe(message => message.To == "Alice@Example.com");

        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        using (var resend = await ResendAsync("  ALICE@example.COM"))
        {
            resend.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await DispatchOutboxAsync();
        Factory.EmailSender.Sent.Count.ShouldBe(3);
        Factory.EmailSender.Sent[^1].Subject.ShouldBe(ConfirmSubject);
    }

    [Fact]
    public async Task Resend_within_cooldown_sends_nothing_but_returns_202()
    {
        await RegisterAndReadTokenAsync("alice@example.com");
        Factory.EmailSender.Clear();

        await AssertResendAcceptedAsync("alice@example.com");
        Factory.Time.Advance(TimeSpan.FromSeconds(59));
        await AssertResendAcceptedAsync("alice@example.com");
        await DispatchOutboxAsync();
        Factory.EmailSender.Sent.ShouldBeEmpty();

        // The cooldown is 60 seconds from the last code: exactly then, a new one goes out.
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        await AssertResendAcceptedAsync("alice@example.com");
        await DispatchOutboxAsync();
        Factory.EmailSender.Sent.ShouldHaveSingleItem().Subject.ShouldBe(ConfirmSubject);
    }

    [Fact]
    public async Task Resend_for_unknown_email_returns_202_and_sends_nothing()
    {
        await AssertResendAcceptedAsync("nobody@example.com");

        await DispatchOutboxAsync();
        Factory.EmailSender.Sent.ShouldBeEmpty();
        (await QueryAsync(context => context.Set<VerificationCode>().CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Resend_for_a_confirmed_email_returns_202_and_sends_nothing()
    {
        var token = await RegisterAndReadTokenAsync("alice@example.com");
        using (var confirm = await ConfirmAsync(token))
        {
            confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        Factory.EmailSender.Clear();
        Factory.Time.Advance(TimeSpan.FromMinutes(5));

        await AssertResendAcceptedAsync("alice@example.com");

        await DispatchOutboxAsync();
        Factory.EmailSender.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Registered_user_has_the_User_role_and_the_requested_locale()
    {
        using var response = await RegisterAsync("alice@example.com", locale: "ms");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var user = await FindUserAsync("alice@example.com");
        user.Locale.ShouldBe("ms");
        user.DisplayName.ShouldBe("Alice");
        var userRole = await QueryAsync(context => context.Set<Role>().SingleAsync(role => role.NormalizedName == Role.NormalizeName(SystemRoles.User), Ct));
        user.Roles.ShouldHaveSingleItem().RoleId.ShouldBe(userRole.Id);
    }

    [Fact]
    public async Task Locale_defaults_to_the_request_language()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RegisterRoute)
        {
            Content = JsonContent.Create(new { email = "alice@example.com", password = Password, displayName = "Alice" }),
        };
        request.Headers.AcceptLanguage.ParseAdd("zh-Hans");

        using var response = await Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await FindUserAsync("alice@example.com")).Locale.ShouldBe("zh-Hans");
    }

    [Theory]
    [InlineData(129)]
    [InlineData(1024 * 1024)]
    public async Task Oversized_password_is_rejected_with_400_and_creates_nothing(int length)
    {
        using var response = await RegisterAsync("alice@example.com", password: new string('p', length));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("errors").TryGetProperty("password", out _).ShouldBeTrue();
        body.GetRawText().ShouldNotContain("pppppppppppp");
        (await QueryAsync(context => context.Set<User>().CountAsync(Ct))).ShouldBe(0);
    }

    [Theory]
    [InlineData(RegisterRoute)]
    [InlineData(ConfirmRoute)]
    [InlineData(ResendRoute)]
    public async Task Rate_limit_returns_429_with_retry_after_for_register(string route)
    {
        const int PermitLimit = 3;
        await using var limited = Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:AuthStrictPermitLimit", PermitLimit.ToString(CultureInfo.InvariantCulture)));
        using var client = limited.CreateClient();

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

    [Fact]
    public async Task Outbox_message_does_not_contain_the_plaintext_token()
    {
        var token = await RegisterAndReadTokenAsync("alice@example.com");

        var contents = await QueryAsync(context => context.Set<OutboxMessage>().Select(message => message.Content).ToListAsync(Ct));

        contents.ShouldNotBeEmpty();
        contents.ShouldAllBe(content => !content.Contains(token) && !content.Contains(Uri.EscapeDataString(token)));
        var code = await QueryAsync(context => context.Set<VerificationCode>().SingleAsync(Ct));
        code.TokenHash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static object Body(string route, int attempt) => route switch
    {
        RegisterRoute => new { email = $"user{attempt}@example.com", password = Password, displayName = "User" },
        ConfirmRoute => new { token = $"unknown-token-{attempt}" },
        _ => new { email = $"user{attempt}@example.com" },
    };

    private static Dictionary<string, string> Headers(HttpResponseMessage response)
        => response.Headers.Concat(response.Content.Headers)
            .Where(header => !VolatileHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);

    private static async Task AssertInvalidTokenAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("auth.invalid_token");
        body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
    }

    private async Task AssertResendAcceptedAsync(string email)
    {
        using var response = await ResendAsync(email);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
    }

    private Task<HttpResponseMessage> RegisterAsync(string email, string password = Password, string displayName = "Alice", string locale = "en")
        => Client.PostAsJsonAsync(RegisterRoute, new { email, password, displayName, locale }, Ct);

    /// <summary>A registration from its own client address, so the per-address limit never decides the outcome.</summary>
    private async Task<HttpResponseMessage> RegisterFromAsync(string email, string clientAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RegisterRoute)
        {
            Content = JsonContent.Create(new { email, password = Password, displayName = "Alice", locale = "en" }),
        };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, clientAddress);
        return await Client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> ConfirmAsync(string token) => Client.PostAsJsonAsync(ConfirmRoute, new { token }, Ct);

    private Task<HttpResponseMessage> ResendAsync(string email) => Client.PostAsJsonAsync(ResendRoute, new { email }, Ct);

    /// <summary>Registers, delivers the confirmation email and returns the token from its link.</summary>
    private async Task<string> RegisterAndReadTokenAsync(string email)
    {
        using var response = await RegisterAsync(email);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await DispatchOutboxAsync();
        return Factory.EmailSender.LastLinkToken(email);
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
            .Include(user => user.Roles)
            .SingleAsync(user => user.NormalizedEmail == User.NormalizeEmail(email), Ct));

    private Task<List<string>> AuditEventsAsync()
        => QueryAsync(context => context.Set<AuthAuditLog>().OrderBy(entry => entry.Id).Select(entry => entry.EventType).ToListAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }
}
