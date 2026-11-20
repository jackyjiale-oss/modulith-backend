using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Web.Common.Concurrency;

namespace TemplateName.IntegrationTests.Documentation;

/// <summary>
/// Every <c>PUT</c> under <c>/api/v1/</c> returns an <c>ETag</c> and takes <c>If-Match</c> (ADR 0038), so a new update cannot forget the
/// decision silently: it opts in (<c>docs/coding-conventions.md</c>) or joins <see cref="ExcludedPuts"/> with its reason. Taking it means
/// the route handler binds a parameter with <c>[FromHeader(Name = "If-Match")]</c>; the ETag means <c>.ProducesEntityTag()</c>.
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
            .Where(endpoint => !ExcludedPuts.Contains(endpoint.RoutePattern.RawText!, StringComparer.Ordinal))
            .ToList();

        puts.ShouldNotBeEmpty();
        puts.Where(endpoint => endpoint.Metadata.GetMetadata<EntityTagMetadata>() is null)
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ShouldBeEmpty("these PUT endpoints declare no ETag (.ProducesEntityTag())");
        puts.Where(endpoint => !BindsIfMatch(endpoint))
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ShouldBeEmpty("these PUT endpoints bind no [FromHeader(Name = \"If-Match\")] parameter");
    }

    // Minimal APIs put the route handler's MethodInfo in the endpoint metadata.
    private static bool BindsIfMatch(RouteEndpoint endpoint)
        => endpoint.Metadata.GetMetadata<MethodInfo>()?.GetParameters()
            .Any(parameter => parameter.GetCustomAttributes().OfType<IFromHeaderMetadata>()
                .Any(header => string.Equals(header.Name, HeaderNames.IfMatch, StringComparison.OrdinalIgnoreCase))) == true;
}
