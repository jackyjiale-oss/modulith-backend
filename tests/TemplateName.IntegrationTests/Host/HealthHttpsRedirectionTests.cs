using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Host;

/// <summary>
/// HTTPS redirection is on outside Development and, once an https port is known (<c>HTTPS_PORT</c> here; the ASP.NET Core Module sets
/// its own on IIS), redirects plain-HTTP requests. The health endpoints are exempt so a plain local probe works; everything else
/// still redirects.
/// </summary>
public sealed class HealthHttpsRedirectionTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health/live/")]
    [InlineData("/HEALTH/live")]
    public async Task Health_paths_answer_over_plain_http(string path)
    {
        await using var configured = WithHttpsPort();
        using var client = CreateClientWithoutRedirects(configured);

        using var response = await client.GetAsync(path, Ct);

        response.StatusCode.ShouldNotBe(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location.ShouldBeNull();
        if (path.TrimEnd('/').Equals("/health/live", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/healthcheck")]
    [InlineData("/healthy")]
    public async Task Other_paths_still_redirect_to_https(string path)
    {
        await using var configured = WithHttpsPort();
        using var client = CreateClientWithoutRedirects(configured);

        using var response = await client.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location.ShouldNotBeNull().ToString().ShouldStartWith("https://localhost:5443/");
    }

    private WebApplicationFactory<Program> WithHttpsPort()
        => Factory.WithWebHostBuilder(builder => builder.UseSetting("HTTPS_PORT", "5443"));

    private static HttpClient CreateClientWithoutRedirects(WebApplicationFactory<Program> configured)
        => configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
