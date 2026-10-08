using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.ArchitectureTests;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Documentation;

/// <summary>
/// Enforces review Section 11.3: every mapped <c>/api/v1/{module}/…</c> endpoint, and every administration endpoint
/// <c>/api/v1/admin/{module}/…</c>, appears in its module's document as <c>METHOD /route</c> (<see cref="DocumentPaths.ModuleOfApiRoute"/>).
/// </summary>
public sealed partial class EndpointDocumentationTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string ApiPrefix = "/api/v1/";

    [Fact]
    public void Every_module_endpoint_is_documented()
    {
        var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(method => (
                Module: DocumentPaths.ModuleOfApiRoute(endpoint.RoutePattern.RawText!),
                Endpoint: $"{method} {Normalize(endpoint.RoutePattern.RawText!)}")))
            .ToList();
        var failures = new List<string>();

        foreach (var module in endpoints.GroupBy(endpoint => endpoint.Module, StringComparer.OrdinalIgnoreCase))
        {
            var path = DocumentPaths.ModuleDocument(module.Key);
            if (!File.Exists(path))
            {
                failures.Add($"{path} is missing; it must document {string.Join(", ", module.Select(endpoint => endpoint.Endpoint))}");
                continue;
            }

            var documented = DocumentedEndpoint().Matches(File.ReadAllText(path))
                .Select(match => $"{match.Groups["method"].Value} {Normalize(match.Groups["route"].Value)}")
                .ToHashSet(StringComparer.Ordinal);
            failures.AddRange(module
                .Where(endpoint => !documented.Contains(endpoint.Endpoint))
                .Select(endpoint => $"{Path.GetFileName(path)} does not document {endpoint.Endpoint}"));
        }

        endpoints.ShouldNotBeEmpty();
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    /// <summary>A group's root route is <c>/api/v1/sample/leave-requests/</c> in the route pattern; documents write it without the slash.</summary>
    private static string Normalize(string route) => route.TrimEnd('/');

    /// <summary>A whole <c>METHOD /api/v1/…</c> route; it ends at whitespace, a backtick, a table pipe, a query string or a closing parenthesis.</summary>
    [GeneratedRegex(@"\b(?<method>GET|POST|PUT|PATCH|DELETE) (?<route>/api/v1/[^\s`|?)]+)")]
    private static partial Regex DocumentedEndpoint();
}
