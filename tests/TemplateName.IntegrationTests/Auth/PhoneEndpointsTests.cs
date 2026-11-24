using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Security;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The phone number routes through the running host: off by default (404), and with <c>Auth:Phone:Enabled</c> the request, the code
/// read from the outbox as Notifications would receive it, the confirmation, the cooldown and a number another account holds.
/// </summary>
public sealed class PhoneEndpointsTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string PhoneRoute = "/api/v1/auth/me/phone";
    private const string ConfirmRoute = "/api/v1/auth/me/phone/confirm";
    private const string MeRoute = "/api/v1/auth/me";
    private const string Number = "+60123456789";

    [Fact]
    public async Task Routes_are_404_while_disabled()
    {
        await SignInAsync();

        using var request = await Client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct);
        using var confirm = await Client.PostAsJsonAsync(ConfirmRoute, new { code = "123456" }, Ct);
        using var remove = await Client.DeleteAsync(PhoneRoute, Ct);

        await AssertProblemAsync(request, HttpStatusCode.NotFound, "http.404");
        await AssertProblemAsync(confirm, HttpStatusCode.NotFound, "http.404");
        await AssertProblemAsync(remove, HttpStatusCode.NotFound, "http.404");
    }

    [Fact]
    public async Task Request_then_confirm_with_the_code_from_the_event()
    {
        await using var host = EnabledHost();
        await SignInAsync();
        using var client = await CreateClientAsync(host);

        using var requested = await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = "+60 12-345 6789" }, Ct);

        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await requested.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("expiresAt").GetDateTimeOffset()
            .ShouldBe(Factory.Time.GetUtcNow().AddMinutes(10));
        using var confirmed = await client.PostAsJsonAsync(ConfirmRoute, new { code = await LatestCodeAsync(host) }, Ct);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var me = await GetMeAsync(client);
        me.GetProperty("phoneNumber").GetString().ShouldBe(Number);
        me.GetProperty("phoneNumberVerifiedAt").GetDateTimeOffset().ShouldBe(Factory.Time.GetUtcNow());
    }

    [Fact]
    public async Task Get_me_shows_no_number_until_one_is_verified()
    {
        await using var host = EnabledHost();
        await SignInAsync();
        using var client = await CreateClientAsync(host);

        using var requested = await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct);

        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var me = await GetMeAsync(client);
        me.GetProperty("phoneNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        me.GetProperty("phoneNumberVerifiedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_same_number_in_another_format_is_taken()
    {
        await using var host = EnabledHost();
        var holder = await CreateUserAsync("holder@example.com");
        await SetPhoneNumberAsync(holder.Id, Number);
        var caller = await SignInAsync();
        await SetPhoneNumberAsync(caller.UserId, "+60198765432");
        using var client = await CreateClientAsync(host);

        using var requested = await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = "+60 (12) 345-6789" }, Ct);
        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var confirmed = await client.PostAsJsonAsync(ConfirmRoute, new { code = await LatestCodeAsync(host) }, Ct);

        await AssertProblemAsync(confirmed, HttpStatusCode.Conflict, "auth.phone_number_taken");
        (await GetMeAsync(client)).GetProperty("phoneNumber").GetString().ShouldBe("+60198765432");
    }

    [Fact]
    public async Task A_second_request_inside_the_cooldown_is_429_with_retry_after()
    {
        await using var host = EnabledHost();
        await SignInAsync();
        using var client = await CreateClientAsync(host);
        using var first = await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Factory.Time.Advance(TimeSpan.FromSeconds(15));

        using var second = await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct);

        await AssertProblemAsync(second, HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
        second.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task An_invalid_number_and_a_wrong_code_are_400()
    {
        await using var host = EnabledHost();
        await SignInAsync();
        using var client = await CreateClientAsync(host);

        using var landline = await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = "+60321234567" }, Ct);
        using var requested = await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct);
        var code = await LatestCodeAsync(host);
        using var wrong = await client.PostAsJsonAsync(ConfirmRoute, new { code = code == "000000" ? "000001" : "000000" }, Ct);

        await AssertProblemAsync(landline, HttpStatusCode.BadRequest, "auth.invalid_phone_number");
        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.invalid_code");
    }

    [Fact]
    public async Task Removing_a_verified_number_clears_it()
    {
        await using var host = EnabledHost();
        var caller = await SignInAsync();
        await SetPhoneNumberAsync(caller.UserId, Number);
        using var client = await CreateClientAsync(host);

        using var removed = await client.DeleteAsync(PhoneRoute, Ct);

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetMeAsync(client)).GetProperty("phoneNumber").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private WebApplicationFactory<Program> EnabledHost()
        => Factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:Phone:Enabled", "true"));

    private async Task<JsonElement> GetMeAsync(HttpClient client)
    {
        using var response = await client.GetAsync(MeRoute, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>The plain code of the newest request, decrypted from its outbox row as the consumer would decrypt it.</summary>
    private async Task<string> LatestCodeAsync(WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var message = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(row => row.Type == typeof(PhoneVerificationRequestedDomainEvent).FullName)
            .OrderByDescending(row => row.OccurredAt)
            .FirstAsync(Ct);
        using var content = JsonDocument.Parse(message.Content);
        var protectedCode = content.RootElement.EnumerateObject()
            .Single(property => string.Equals(property.Name, "ProtectedCode", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;

        return scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(protectedCode);
    }

    private async Task SetPhoneNumberAsync(Guid userId, string number)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await context.Set<User>().SingleAsync(candidate => candidate.Id == userId, Ct);
        user.VerifyPhoneNumber(number, Factory.Time.GetUtcNow());
        await context.SaveChangesAsync(Ct);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(code);
    }
}
