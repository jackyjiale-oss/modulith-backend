using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemplateName.Web.Common.Security;

namespace TemplateName.UnitTests.Web;

/// <summary><c>AddBearerSecurity</c>, through the real OpenAPI document generator.</summary>
public sealed class OpenApiSecurityTests
{
    [Fact]
    public async Task Document_declares_the_bearer_scheme()
    {
        using var document = await GetDocumentAsync();

        var scheme = document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty(OpenApiSecurity.SchemeName);
        scheme.GetProperty("type").GetString().ShouldBe("http");
        scheme.GetProperty("scheme").GetString().ShouldBe("bearer");
        scheme.GetProperty("bearerFormat").GetString().ShouldBe("JWT");
    }

    [Theory]
    [InlineData("/authorized")]
    [InlineData("/permission")]
    public async Task Operation_with_authorization_data_requires_the_bearer_scheme(string path)
    {
        using var document = await GetDocumentAsync();

        var security = Operation(document, path).GetProperty("security");
        security.GetArrayLength().ShouldBe(1);
        var requirement = security[0];
        requirement.EnumerateObject().Select(property => property.Name).ShouldBe([OpenApiSecurity.SchemeName]);
        requirement.GetProperty(OpenApiSecurity.SchemeName).GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("/anonymous")]
    [InlineData("/authorized-but-anonymous")]
    [InlineData("/no-metadata")]
    public async Task Anonymous_operation_or_one_without_authorization_data_has_no_security_requirement(string path)
    {
        using var document = await GetDocumentAsync();

        Operation(document, path).TryGetProperty("security", out _).ShouldBeFalse();
    }

    private static JsonElement Operation(JsonDocument document, string path)
        => document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get");

    private static async Task<JsonDocument> GetDocumentAsync()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services => services
                    .AddRouting()
                    .AddAuthorization()
                    .AddOpenApi("v1", options => options.AddBearerSecurity()))
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/authorized", () => Results.Ok()).RequireAuthorization();
                        endpoints.MapGet("/permission", () => Results.Ok()).RequirePermission("test.resource.read");
                        endpoints.MapGet("/anonymous", () => Results.Ok()).AllowAnonymous();
                        endpoints.MapGet("/authorized-but-anonymous", () => Results.Ok()).RequireAuthorization().AllowAnonymous();
                        endpoints.MapGet("/no-metadata", () => Results.Ok());
                        endpoints.MapOpenApi();
                    });
                }))
            .StartAsync(TestContext.Current.CancellationToken);
        using var client = host.GetTestClient();

        var body = await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body);
    }
}
