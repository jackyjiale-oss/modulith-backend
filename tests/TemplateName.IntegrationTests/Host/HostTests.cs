using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Host;

public sealed class HostTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private static readonly (string Name, string Value)[] ExpectedSecurityHeaders =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("Referrer-Policy", "no-referrer"),
        ("X-Frame-Options", "DENY"),
        ("Content-Security-Policy", "frame-ancestors 'none'"),
        ("Permissions-Policy", "camera=(), microphone=(), geolocation=()"),
    ];

    [Fact]
    public async Task Live_returns_200()
    {
        using var response = await Client.GetAsync("/health/live", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_returns_200()
    {
        using var response = await Client.GetAsync("/health/ready", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_route_returns_404_problem_details_with_trace_id()
    {
        using var response = await Client.GetAsync("/api/v1/does-not-exist", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
        body.GetProperty("code").GetString().ShouldBe("http.404");
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Unhandled_exception_returns_500_problem_details_with_trace_id_and_security_headers()
    {
        await using var throwing = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton<IStartupFilter, ThrowingEndpointStartupFilter>()));
        using var client = throwing.CreateClient();

        using var response = await client.GetAsync("/throw", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task OpenApi_document_is_served_outside_production()
    {
        using var response = await Client.GetAsync("/openapi/v1.json", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Scalar_is_served_outside_production()
    {
        using var response = await Client.GetAsync("/scalar/v1", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Scalar_is_not_mapped_in_production()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var production = Factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("Auth:Jwt:SigningKeys:0:KeyId", "production-test")
            .UseSetting("Auth:Jwt:SigningKeys:0:PrivateKeyPem", signingKey.ExportPkcs8PrivateKeyPem()));
        using var client = production.CreateClient();

        using var response = await client.GetAsync("/scalar/v1", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Production_does_not_start_without_a_signing_key()
    {
        await using var production = Factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var failure = Should.Throw<OptionsValidationException>(() => production.CreateClient());

        failure.Message.ShouldContain("Auth:Jwt:SigningKeys");
    }

    [Fact]
    public async Task Security_headers_are_present()
    {
        using var response = await Client.GetAsync("/health/live", Ct);

        AssertSecurityHeaders(response);
    }

    [Fact]
    public void Kestrel_request_body_limit_is_bound_from_configuration()
    {
        var options = Factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        options.Limits.MaxRequestBodySize.ShouldBe(10485760);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        foreach (var (name, value) in ExpectedSecurityHeaders)
        {
            response.Headers.GetValues(name).ShouldHaveSingleItem().ShouldBe(value);
        }
    }

    /// <summary>Adds a terminal <c>/throw</c> branch inside the production pipeline so the exception handler has something to catch.</summary>
    private sealed class ThrowingEndpointStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map("/throw", branch => branch.Run(_ => throw new InvalidOperationException("boom")));
        };
    }
}
