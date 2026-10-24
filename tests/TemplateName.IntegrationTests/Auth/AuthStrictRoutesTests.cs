using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Web.Common.Security;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// Pins which routes of the real host carry the <c>auth-strict</c> rate-limit policy. A route that checks a password, a code or a
/// token must keep it (the Redis limiter inherits this list), and a new route that gets it must be added here and to
/// <c>docs/modules/auth.md</c> and <c>docs/services/api.md</c>. It only reads the route table of the shared host, so it does not derive from
/// <see cref="IntegrationTestBase"/> and the databases are not reset for it.
/// </summary>
public sealed class AuthStrictRoutesTests(IntegrationTestWebAppFactory factory)
{
    private static readonly string[] ExpectedRoutes =
    [
        "POST /api/v1/auth/email/confirm",
        "POST /api/v1/auth/email/resend-confirmation",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/mfa/verify",
        "POST /api/v1/auth/password/change",
        "POST /api/v1/auth/password/forgot",
        "POST /api/v1/auth/password/reset",
        "POST /api/v1/auth/register",
        "POST /api/v1/auth/step-up",
        "POST /api/v1/auth/token/refresh",
    ];

    [Fact]
    public void Exactly_ten_routes_carry_auth_strict_among_them_mfa_verify_and_step_up()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        var strict = endpoints
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<EnableRateLimitingAttribute>()
                .Any(attribute => attribute.PolicyName == RateLimitPolicies.AuthStrict))
            .SelectMany(endpoint => HttpMethodsOf(endpoint)
                .Select(method => $"{method} /{endpoint.RoutePattern.RawText!.TrimStart('/')}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        strict.ShouldBe(ExpectedRoutes);
    }

    private static IReadOnlyList<string> HttpMethodsOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
        ?? throw new InvalidOperationException($"The rate-limited endpoint '{endpoint.DisplayName}' has no HTTP method metadata, so it cannot be listed.");
}
