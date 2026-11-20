using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Web.Common.Concurrency;

namespace TemplateName.IntegrationTests.Documentation;

/// <summary>
/// Every <c>PUT</c> under <c>/api/v1/</c> returns an <c>ETag</c> and takes <c>If-Match</c> (ADR 0038), so a new update cannot forget the
/// decision silently: it opts in (<c>docs/coding-conventions.md</c>) or joins <see cref="ExcludedPuts"/> with its reason.
/// </summary>
public sealed class EntityTagCoverageTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string ApiPrefix = "/api/v1/";

    // Route patterns of PUT endpoints that deliberately take no If-Match, each with a comment saying why. Empty: every PUT takes it.
    private static readonly string[] ExcludedPuts = [];

    [Fact]
    public void Every_put_endpoint_returns_an_etag_and_takes_if_match()
    {
        var puts = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase) == true)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Put) == true)
            .ToList();

        puts.ShouldNotBeEmpty();
        puts.Where(endpoint => endpoint.Metadata.GetMetadata<EntityTagMetadata>() is null)
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Where(route => !ExcludedPuts.Contains(route, StringComparer.Ordinal))
            .ShouldBeEmpty();
    }
}
