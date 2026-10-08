using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The rate limiter (pipeline slot 9a) runs before authorization (slot 10), so a request that authorization turns away is still
/// counted: an anonymous 401 on a protected or unknown route, and a signed-in 403 from <c>RequirePermission</c>. Each test uses a host
/// with a limit of two requests, so the third request is the first one rejected.
/// </summary>
public sealed class RateLimitOrderTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const int PermitLimit = 2;

    [Fact]
    public async Task Anonymous_requests_to_a_protected_route_are_counted_and_limited_with_429()
    {
        await using var limited = LimitedFactory();
        using var client = limited.CreateClient();

        await AssertCountedThenLimitedAsync(client, ProtectedTestEndpointStartupFilter.Route, withToken: null, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Anonymous_requests_to_an_unknown_route_are_counted_and_limited_with_429()
    {
        await using var limited = LimitedFactory();
        using var client = limited.CreateClient();

        await AssertCountedThenLimitedAsync(client, "/api/v1/does-not-exist", withToken: null, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Signed_in_requests_that_lack_the_permission_are_counted_and_limited_with_429()
    {
        await using var limited = LimitedFactory();
        using var client = limited.CreateClient();
        var token = TestAccessTokens.Issue(limited.Services).Value;

        await AssertCountedThenLimitedAsync(client, ProtectedTestEndpointStartupFilter.PermissionRoute, token, HttpStatusCode.Forbidden);
    }

    private async Task AssertCountedThenLimitedAsync(
        HttpClient client,
        string path,
        string? withToken,
        HttpStatusCode authorizationRejection)
    {
        for (var attempt = 0; attempt < PermitLimit; attempt++)
        {
            using var rejected = await client.SendAsync(Request(path, withToken), Ct);
            rejected.StatusCode.ShouldBe(authorizationRejection);
        }

        using var response = await client.SendAsync(Request(path, withToken), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.Contains("Retry-After").ShouldBeTrue();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetInt32().ShouldBe(429);
        body.GetProperty("code").GetString().ShouldBe("rate_limit.exceeded");
    }

    private static HttpRequestMessage Request(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        return token is null ? request : request.WithBearer(token);
    }

    private WebApplicationFactory<Program> LimitedFactory()
        => Factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:GlobalPermitLimit", PermitLimit.ToString(CultureInfo.InvariantCulture)));
}
