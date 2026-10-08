using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The whole account lifecycle through the running host, with the real database, outbox, hasher and token issuer and only the
/// email transport replaced by <see cref="RecordingEmailSender"/>: one test that follows an account from registration to logout.
/// </summary>
public sealed class AuthFlowTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string Email = "flow@example.com";
    private const string FirstPassword = "correct horse battery staple";
    private const string SecondPassword = "second horse battery staple";
    private const string ThirdPassword = "third horse battery staple";

    private int _requests;

    [Fact]
    public async Task Full_account_lifecycle()
    {
        // Register: 202 whatever the address, then the outbox delivers the confirmation link.
        using (var register = await PostAsync("/api/v1/auth/register", new { email = Email, password = FirstPassword, displayName = "Flow" }))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await DispatchOutboxAsync();
        var confirmationToken = Factory.EmailSender.LastLinkToken(Email);

        // Before confirmation the right password is not enough.
        using (var early = await PostAsync("/api/v1/auth/login", new { email = Email, password = FirstPassword }))
        {
            early.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ProblemCodeAsync(early)).ShouldBe("auth.email_not_verified");
        }

        using (var confirm = await PostAsync("/api/v1/auth/email/confirm", new { token = confirmationToken }))
        {
            confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Two sessions on two devices.
        var first = await LoginAsync(FirstPassword, "laptop");
        var second = await LoginAsync(FirstPassword, "phone");
        first.SessionId.ShouldNotBe(second.SessionId);

        using (var me = await GetAsync("/api/v1/auth/me", first.AccessToken))
        {
            me.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("email").GetString().ShouldBe(Email);
            body.GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
            body.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ShouldBe(["User"]);
        }

        // Refresh rotates the token inside the same session; the old one is used up.
        var rotated = await RefreshAsync(first.RefreshToken);
        rotated.SessionId.ShouldBe(first.SessionId);
        rotated.RefreshToken.ShouldNotBe(first.RefreshToken);
        rotated.AccessToken.ShouldNotBe(first.AccessToken);

        // Changing the password on the laptop ends the phone's session and keeps the laptop's.
        using (var change = await PostAsync("/api/v1/auth/password/change", new { currentPassword = FirstPassword, newPassword = SecondPassword }, rotated.AccessToken))
        {
            change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var phone = await PostAsync("/api/v1/auth/token/refresh", new { refreshToken = second.RefreshToken }))
        {
            phone.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await ProblemCodeAsync(phone)).ShouldBe("auth.invalid_refresh_token");
        }

        var laptop = await RefreshAsync(rotated.RefreshToken);
        laptop.SessionId.ShouldBe(first.SessionId);
        using (var meAfterChange = await GetAsync("/api/v1/auth/me", laptop.AccessToken))
        {
            meAfterChange.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Forgot and reset: the link arrives through the outbox, the reset ends every session, and only the new password works.
        using (var forgot = await PostAsync("/api/v1/auth/password/forgot", new { email = Email }))
        {
            forgot.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await DispatchOutboxAsync();
        var resetToken = Factory.EmailSender.LastLinkToken(Email);
        resetToken.ShouldNotBe(confirmationToken);

        using (var reset = await PostAsync("/api/v1/auth/password/reset", new { token = resetToken, newPassword = ThirdPassword }))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var endedByReset = await PostAsync("/api/v1/auth/token/refresh", new { refreshToken = laptop.RefreshToken }))
        {
            endedByReset.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await ProblemCodeAsync(endedByReset)).ShouldBe("auth.invalid_refresh_token");
        }

        foreach (var staleBefore in new[] { FirstPassword, SecondPassword })
        {
            using var stale = await PostAsync("/api/v1/auth/login", new { email = Email, password = staleBefore });
            stale.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await ProblemCodeAsync(stale)).ShouldBe("auth.invalid_credentials");
        }

        // A new sign-in with the new password, then logout ends it: its refresh token is refused.
        var fresh = await LoginAsync(ThirdPassword, "desktop");
        using (var logout = await PostAsync("/api/v1/auth/logout", body: null, fresh.AccessToken))
        {
            logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var afterLogout = await PostAsync("/api/v1/auth/token/refresh", new { refreshToken = fresh.RefreshToken }))
        {
            afterLogout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await ProblemCodeAsync(afterLogout)).ShouldBe("auth.invalid_refresh_token");
        }
    }

    private async Task<Tokens> LoginAsync(string password, string deviceName)
    {
        using var response = await PostAsync("/api/v1/auth/login", new { email = Email, password, deviceName });
        return await ReadTokensAsync(response);
    }

    private async Task<Tokens> RefreshAsync(string refreshToken)
    {
        using var response = await PostAsync("/api/v1/auth/token/refresh", new { refreshToken });
        return await ReadTokensAsync(response);
    }

    private static async Task<Tokens> ReadTokensAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Tokens(
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!,
            body.GetProperty("sessionId").GetGuid());
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("code").GetString();
    }

    // Every request comes from its own client address, so the strict per-address limit of the anonymous routes is never what
    // this test measures.
    private async Task<HttpResponseMessage> PostAsync(string route, object? body, string? accessToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await SendAsync(request, accessToken);
    }

    private async Task<HttpResponseMessage> GetAsync(string route, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        return await SendAsync(request, accessToken);
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string? accessToken)
    {
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, $"10.20.0.{Interlocked.Increment(ref _requests)}");
        if (accessToken is not null)
        {
            request.WithBearer(accessToken);
        }

        return Client.SendAsync(request, Ct);
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

    private sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);
}
