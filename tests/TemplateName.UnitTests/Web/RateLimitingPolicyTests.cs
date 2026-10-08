using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TemplateName.Web.Common;
using TemplateName.Web.Common.Security;

namespace TemplateName.UnitTests.Web;

/// <summary>The named rate-limit policies <c>AddHttpSecurity</c> registers, exercised through the real rate-limiting middleware.</summary>
public sealed class RateLimitingPolicyTests
{
    private const string StrictRoute = "/strict";
    private const string AddressHeader = "Test-Remote-Address";

    [Fact]
    public void AuthStrict_policy_name_is_auth_strict()
        => RateLimitPolicies.AuthStrict.ShouldBe("auth-strict");

    [Fact]
    public async Task AuthStrict_policy_is_registered_with_the_configured_limit()
    {
        using var host = await StartHostAsync(new Dictionary<string, string?> { ["RateLimiting:AuthStrictPermitLimit"] = "2" });
        using var client = host.GetTestClient();

        var statuses = await GetStatusesAsync(client, "203.0.113.1", "203.0.113.1", "203.0.113.1");

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [Fact]
    public async Task AuthStrict_policy_defaults_to_ten_requests_per_minute()
    {
        using var host = await StartHostAsync([]);
        var options = host.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        using var client = host.GetTestClient();

        var statuses = await GetStatusesAsync(client, [.. Enumerable.Repeat("203.0.113.1", 11)]);

        options.AuthStrictPermitLimit.ShouldBe(10);
        options.AuthStrictWindow.ShouldBe(TimeSpan.FromMinutes(1));
        statuses.Count(status => status == HttpStatusCode.OK).ShouldBe(10);
        statuses[^1].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task AuthStrict_policy_is_partitioned_by_client_address()
    {
        using var host = await StartHostAsync(new Dictionary<string, string?> { ["RateLimiting:AuthStrictPermitLimit"] = "1" });
        using var client = host.GetTestClient();

        var statuses = await GetStatusesAsync(client, "203.0.113.1", "203.0.113.2", "203.0.113.1");

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [Fact]
    public async Task AuthStrict_rejection_is_the_rate_limit_problem()
    {
        using var host = await StartHostAsync(new Dictionary<string, string?> { ["RateLimiting:AuthStrictPermitLimit"] = "1" });
        using var client = host.GetTestClient();
        _ = await GetStatusesAsync(client, "203.0.113.1");

        using var request = StrictRequest("203.0.113.1");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.Contains("Retry-After").ShouldBeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("code").GetString().ShouldBe("rate_limit.exceeded");
    }

    [Theory]
    [InlineData("RateLimiting:AuthStrictPermitLimit", "0")]
    [InlineData("RateLimiting:AuthStrictWindow", "00:00:00")]
    public void Invalid_auth_strict_settings_fail_validation(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        using var services = new ServiceCollection().AddHttpSecurity(configuration).BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<RateLimitingOptions>>().Value);
    }

    private static async Task<List<HttpStatusCode>> GetStatusesAsync(HttpClient client, params string[] addresses)
    {
        var statuses = new List<HttpStatusCode>();
        foreach (var address in addresses)
        {
            using var request = StrictRequest(address);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }

    private static HttpRequestMessage StrictRequest(string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, StrictRoute);
        request.Headers.Add(AddressHeader, address);
        return request;
    }

    private static Task<IHost> StartHostAsync(Dictionary<string, string?> settings)
        => new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings))
                .ConfigureServices((context, services) => services.AddRouting().AddWebCommon().AddHttpSecurity(context.Configuration))
                .Configure(app =>
                {
                    // TestServer gives no remote address; take it from a header so partitions can be told apart.
                    app.Use((context, next) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[AddressHeader].ToString());
                        return next(context);
                    });
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints => endpoints.MapGet(StrictRoute, () => Results.Ok()).RequireRateLimiting(RateLimitPolicies.AuthStrict));
                }))
            .StartAsync(TestContext.Current.CancellationToken);
}
