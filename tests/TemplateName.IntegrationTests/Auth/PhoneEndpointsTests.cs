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
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The phone number routes through the running host: off by default (404), and with <c>Auth:Phone:Enabled</c> the request, the code
/// read from the outbox as a consuming module would receive it, the confirmation, the cooldown and a number another account holds.
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
    public async Task Parallel_requests_of_one_user_issue_one_code()
    {
        await using var host = EnabledHost();
        var caller = await SignInAsync();
        using var client = await CreateClientAsync(host);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct)));

        // The user's row is written with each code, so its RowVersion lets one request through and refuses the others (409 or 429).
        responses.Count(response => response.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Accepted
            || response.StatusCode == HttpStatusCode.Conflict
            || response.StatusCode == HttpStatusCode.TooManyRequests);
        (await CodeCountAsync(caller.UserId)).ShouldBe(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Two_users_confirming_the_same_number_get_one_204_and_one_409()
    {
        await using var host = EnabledHost();
        var first = await SignInAsync();
        using var firstClient = await CreateClientAsync(host);
        var second = await SignInAsync();
        using var secondClient = await CreateClientAsync(host);
        (await firstClient.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await secondClient.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var firstCode = await CodeForAsync(host, first.UserId);
        var secondCode = await CodeForAsync(host, second.UserId);

        var confirmed = await Task.WhenAll(
            firstClient.PostAsJsonAsync(ConfirmRoute, new { code = firstCode }, Ct),
            secondClient.PostAsJsonAsync(ConfirmRoute, new { code = secondCode }, Ct));

        confirmed.Select(response => response.StatusCode).ShouldBe([HttpStatusCode.NoContent, HttpStatusCode.Conflict], ignoreOrder: true);
        await AssertProblemAsync(confirmed.Single(response => response.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "auth.phone_number_taken");
        foreach (var response in confirmed)
        {
            response.Dispose();
        }
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

    [Fact]
    public async Task First_number_needs_a_step_up()
    {
        await using var host = EnabledHost();
        var caller = await SignInAsync();
        Factory.Time.Advance(TimeSpan.FromMinutes(11));
        using var client = await CreateClientAsync(host);

        using var requested = await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct);

        await AssertProblemAsync(requested, HttpStatusCode.Forbidden, "auth.step_up_required");
        (await CodeCountAsync(caller.UserId)).ShouldBe(0);
    }

    [Fact]
    public async Task Replacing_the_number_signs_out_the_other_sessions()
    {
        await using var host = EnabledHost();
        var caller = await SignInAsync();
        await SetPhoneNumberAsync(caller.UserId, "+60198765432");
        var otherSessionId = await StartOtherSessionAsync(caller.UserId);
        using var client = await CreateClientAsync(host);

        (await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var confirmed = await client.PostAsJsonAsync(ConfirmRoute, new { code = await LatestCodeAsync(host) }, Ct);

        confirmed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var sessions = await SessionsAsync(caller.UserId);
        sessions.Single(session => session.Id == otherSessionId).RevokedReason.ShouldBe(SessionRevokedReason.PhoneNumberChanged);
        sessions.Single(session => session.Id == caller.SessionId).RevokedAt.ShouldBeNull();
        (await GetMeAsync(client)).GetProperty("phoneNumber").GetString().ShouldBe(Number);
    }

    [Fact]
    public async Task Each_change_publishes_one_event()
    {
        await using var host = EnabledHost();
        var caller = await SignInAsync();
        using var client = await CreateClientAsync(host);

        (await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = "+60198765432" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.PostAsJsonAsync(ConfirmRoute, new { code = await LatestCodeAsync(host) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Factory.Time.Advance(TimeSpan.FromMinutes(2));
        (await client.PostAsJsonAsync(PhoneRoute, new { phoneNumber = Number }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.PostAsJsonAsync(ConfirmRoute, new { code = await LatestCodeAsync(host) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        (await client.DeleteAsync(PhoneRoute, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var changes = await PhoneNumberChangesAsync(caller.UserId);
        changes.ShouldBe([(1, "432"), (2, "789"), (3, "789")]);
    }

    private async Task<Guid> StartOtherSessionAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await context.Set<User>().AsNoTracking().SingleAsync(candidate => candidate.Id == userId, Ct);
        var (session, _) = UserSession.Start(
            userId,
            AuthenticationMethods.Password,
            "Laptop",
            userAgent: null,
            ipAddress: null,
            user.SecurityStamp,
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            TimeSpan.FromDays(14),
            TimeSpan.FromDays(90),
            Factory.Time.GetUtcNow());
        context.Add(session);
        await context.SaveChangesAsync(Ct);
        return session.Id;
    }

    private async Task<List<UserSession>> SessionsAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<UserSession>().AsNoTracking()
            .Where(session => session.UserId == userId)
            .ToListAsync(Ct);
    }

    /// <summary>The kind and last digits of every number change of <paramref name="userId"/>, oldest first, from the outbox.</summary>
    private async Task<List<(int Change, string LastDigits)>> PhoneNumberChangesAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(row => row.Type == typeof(PhoneNumberChangedDomainEvent).FullName)
            .OrderBy(row => row.OccurredAt)
            .ToListAsync(Ct);
        var changes = new List<(int Change, string LastDigits)>();
        foreach (var message in messages)
        {
            using var content = JsonDocument.Parse(message.Content);
            var properties = content.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            if (properties["UserId"].GetGuid() == userId)
            {
                changes.Add((properties["Change"].GetInt32(), properties["LastDigits"].GetString()!));
            }
        }

        return changes;
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

    /// <summary>The plain code of <paramref name="userId"/>'s newest request, decrypted from its outbox row.</summary>
    private async Task<string> CodeForAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(row => row.Type == typeof(PhoneVerificationRequestedDomainEvent).FullName)
            .OrderByDescending(row => row.OccurredAt)
            .ToListAsync(Ct);
        foreach (var message in messages)
        {
            using var content = JsonDocument.Parse(message.Content);
            var properties = content.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            if (properties["UserId"].GetGuid() == userId)
            {
                return scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(properties["ProtectedCode"].GetString()!);
            }
        }

        throw new InvalidOperationException("No phone code was requested for the user.");
    }

    private async Task<int> CodeCountAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<PhoneVerificationCode>().CountAsync(code => code.UserId == userId, Ct);
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
