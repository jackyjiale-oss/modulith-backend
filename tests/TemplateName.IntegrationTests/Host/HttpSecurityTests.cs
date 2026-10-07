using System.Net;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Host;

public sealed class HttpSecurityTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string AllowedOrigin = "https://app.example.com";
    private const string UntrustedPeer = "198.51.100.7";
    private const string TestUserHeader = "X-Test-User";

    [Fact]
    public async Task Allowed_origin_gets_cors_headers()
    {
        await using var configured = Factory.WithWebHostBuilder(
            builder => builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin));
        using var client = configured.CreateClient();
        using var request = PreflightRequest(AllowedOrigin);

        using var response = await client.SendAsync(request, Ct);

        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(AllowedOrigin);
        response.Headers.GetValues("Access-Control-Allow-Credentials").ShouldHaveSingleItem().ShouldBe("true");
    }

    [Fact]
    public async Task Unknown_origin_gets_no_cors_headers()
    {
        await using var configured = Factory.WithWebHostBuilder(
            builder => builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin));
        using var client = configured.CreateClient();
        using var request = PreflightRequest("https://evil.example");

        using var response = await client.SendAsync(request, Ct);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public void Wildcard_cors_origin_fails_startup()
    {
        using var configured = Factory.WithWebHostBuilder(
            builder => builder.UseSetting("Cors:AllowedOrigins:0", "*"));

        var exception = Should.Throw<OptionsValidationException>(() => configured.CreateClient());

        exception.Message.ShouldContain("*");
    }

    [Fact]
    public void Non_positive_permit_limit_fails_startup()
    {
        using var configured = Factory.WithWebHostBuilder(
            builder => builder.UseSetting("RateLimiting:GlobalPermitLimit", "0"));

        Should.Throw<OptionsValidationException>(() => configured.CreateClient());
    }

    [Fact]
    public void Malformed_known_network_fails_startup()
    {
        using var configured = Factory.WithWebHostBuilder(
            builder => builder.UseSetting("ForwardedHeaders:KnownNetworks:0", "not-a-network"));

        Should.Throw<OptionsValidationException>(() => configured.CreateClient());
    }

    [Fact]
    public async Task Exceeding_global_limit_returns_429_problem_details()
    {
        await using var limited = LimitedFactory(2);
        using var client = limited.CreateClient();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var allowed = await client.GetAsync("/api/v1/does-not-exist", Ct);
            allowed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var response = await client.GetAsync("/api/v1/does-not-exist", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetInt32().ShouldBe(429);
        body.GetProperty("code").GetString().ShouldBe("rate_limit.exceeded");
        var traceId = response.Headers.GetValues("X-Trace-Id").Single();
        traceId.ShouldMatch("^[0-9a-f]{32}$");
        body.GetProperty("traceId").GetString().ShouldBe(traceId);
        var retryAfterSeconds = int.Parse(response.Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture);
        retryAfterSeconds.ShouldBeInRange(1, 60);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Health_endpoints_are_not_rate_limited()
    {
        await using var limited = LimitedFactory(1);
        using var client = limited.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await client.GetAsync("/health/live", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    /// <remarks>
    /// <c>TestServer</c> gives a request no remote address, and the forwarded-headers middleware then trusts any <c>X-Forwarded-For</c>
    /// (there is no peer to check). Kestrel always supplies the peer address, so the test sets one that is not a trusted proxy.
    /// </remarks>
    [Fact]
    public async Task Spoofed_forwarded_for_does_not_bypass_rate_limit()
    {
        await using var limited = LimitedFactory(2, remoteAddress: UntrustedPeer);
        using var client = limited.CreateClient();

        var statuses = await GetStatusesAsync(client, ("X-Forwarded-For", ["203.0.113.1", "203.0.113.2", "203.0.113.3"]));

        statuses.ShouldBe([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests]);
    }

    [Fact]
    public async Task Forwarded_for_from_a_known_proxy_selects_the_client_partition()
    {
        await using var limited = LimitedFactory(1, remoteAddress: UntrustedPeer, knownProxy: UntrustedPeer);
        using var client = limited.CreateClient();

        var statuses = await GetStatusesAsync(client, ("X-Forwarded-For", ["203.0.113.1", "203.0.113.2", "203.0.113.1"]));

        statuses.ShouldBe([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests]);
    }

    [Fact]
    public async Task Authenticated_users_are_limited_per_subject()
    {
        await using var limited = LimitedFactory(1);
        using var client = limited.CreateClient();

        var statuses = await GetStatusesAsync(client, (TestUserHeader, ["user-a", "user-b", "user-a"]));

        statuses.ShouldBe([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests]);
    }

    private async Task<List<HttpStatusCode>> GetStatusesAsync(HttpClient client, (string Header, string[] Values) requests)
    {
        var statuses = new List<HttpStatusCode>();
        foreach (var value in requests.Values)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/does-not-exist");
            request.Headers.Add(requests.Header, value);
            using var response = await client.SendAsync(request, Ct);
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }

    private static HttpRequestMessage PreflightRequest(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/x");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        response.Headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff");
        response.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY");
    }

    private WebApplicationFactory<Program> LimitedFactory(int permitLimit, string? remoteAddress = null, string? knownProxy = null)
        => Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:GlobalPermitLimit", permitLimit.ToString(CultureInfo.InvariantCulture));
            if (knownProxy is not null)
            {
                builder.UseSetting("ForwardedHeaders:KnownProxies:0", knownProxy);
            }

            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new TestConnectionStartupFilter(remoteAddress)));
        });

    /// <summary>
    /// Runs ahead of the whole pipeline: sets the connection's remote address when given, and authenticates the request as the
    /// user named in the <c>X-Test-User</c> header.
    /// </summary>
    private sealed class TestConnectionStartupFilter(string? remoteAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (remoteAddress is not null)
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
                }

                if (context.Request.Headers.TryGetValue(TestUserHeader, out var subject))
                {
                    var identity = new ClaimsIdentity([new Claim("sub", subject.ToString())], authenticationType: "Test");
                    context.User = new ClaimsPrincipal(identity);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
