using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using TemplateName.Application.Common.Identity;
using TemplateName.Web.Common.Observability;

namespace TemplateName.UnitTests.Web;

public sealed class ObservabilityExtensionsTests
{
    [Theory]
    [InlineData("/health/live", 200, LogEventLevel.Verbose)]
    [InlineData("/health/ready", 200, LogEventLevel.Verbose)]
    [InlineData("/health/ready", 503, LogEventLevel.Error)]
    [InlineData("/api/v1/leave-requests", 200, LogEventLevel.Information)]
    [InlineData("/api/v1/leave-requests", 404, LogEventLevel.Information)]
    [InlineData("/api/v1/leave-requests", 500, LogEventLevel.Error)]
    [InlineData("/healthy-things", 200, LogEventLevel.Information)]
    public void Request_log_level_demotes_only_successful_health_probes(string path, int statusCode, LogEventLevel expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.StatusCode = statusCode;

        var level = ObservabilityExtensions.ResolveRequestLogLevel(context, 1, exception: null);

        level.ShouldBe(expected);
    }

    [Fact]
    public void Request_log_level_is_error_when_an_exception_escaped()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/live";

        var level = ObservabilityExtensions.ResolveRequestLogLevel(context, 1, new InvalidOperationException("boom"));

        level.ShouldBe(LogEventLevel.Error);
    }

    [Fact]
    public void Request_enrichment_adds_the_user_id_of_an_authenticated_user()
    {
        var userId = Guid.NewGuid();
        var diagnosticContext = Substitute.For<IDiagnosticContext>();

        ObservabilityExtensions.EnrichRequest(diagnosticContext, ContextWithUser(userId));

        diagnosticContext.Received(1).Set("UserId", userId, destructureObjects: false);
    }

    [Fact]
    public void Request_enrichment_adds_nothing_for_an_anonymous_request()
    {
        var diagnosticContext = Substitute.For<IDiagnosticContext>();

        ObservabilityExtensions.EnrichRequest(diagnosticContext, ContextWithUser(null));

        diagnosticContext.DidNotReceiveWithAnyArgs().Set(default!, default(object), default);
    }

    [Theory]
    [InlineData("/api/v1/auth/login", 1)]
    [InlineData("/api/v1", 1)]
    [InlineData("/api/v2/x/", 2)]
    [InlineData("/api/v12/x", 12)]
    [InlineData("/API/V1/x", 1)]
    public void Request_enrichment_adds_the_api_version_of_an_api_route(string path, int expected)
    {
        var diagnosticContext = Substitute.For<IDiagnosticContext>();

        ObservabilityExtensions.EnrichRequest(diagnosticContext, ContextFor(path));

        diagnosticContext.Received(1).Set("ApiVersion", expected, destructureObjects: false);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/api/version")]
    [InlineData("/api/v1x/y")]
    [InlineData("/apiv1")]
    [InlineData("/api/v99999999999/x")]
    [InlineData("/api/v١/x")]
    [InlineData("/api/v1\n")]
    public void Request_enrichment_adds_no_api_version_off_the_versioned_api(string path)
    {
        var diagnosticContext = Substitute.For<IDiagnosticContext>();

        ObservabilityExtensions.EnrichRequest(diagnosticContext, ContextFor(path));

        diagnosticContext.DidNotReceive().Set("ApiVersion", Arg.Any<object?>(), Arg.Any<bool>());
    }

    [Theory]
    [InlineData("1.4.2")]
    [InlineData("2.0.0+build.5")]
    [InlineData("abc_DEF-1")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")]
    public void Request_enrichment_adds_a_valid_client_version(string value)
    {
        var diagnosticContext = Substitute.For<IDiagnosticContext>();

        ObservabilityExtensions.EnrichRequest(diagnosticContext, ContextFor("/api/v1/x", value));

        diagnosticContext.Received(1).Set("ClientVersion", value, destructureObjects: false);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    [InlineData("1.0 beta")]
    [InlineData("1.0\r\nX: y")]
    [InlineData("1.0\n")]
    [InlineData("é1")]
    public void Request_enrichment_drops_an_invalid_client_version(string value)
    {
        var diagnosticContext = Substitute.For<IDiagnosticContext>();

        ObservabilityExtensions.EnrichRequest(diagnosticContext, ContextFor("/api/v1/x", value));

        diagnosticContext.DidNotReceive().Set("ClientVersion", Arg.Any<object?>(), Arg.Any<bool>());
    }

    [Fact]
    public void Request_enrichment_uses_the_first_client_version_header_value()
    {
        var diagnosticContext = Substitute.For<IDiagnosticContext>();

        ObservabilityExtensions.EnrichRequest(diagnosticContext, ContextFor("/api/v1/x", "1.0", "2.0"));

        diagnosticContext.Received(1).Set("ClientVersion", "1.0", destructureObjects: false);
    }

    [Fact]
    public async Task Telemetry_providers_are_registered_only_when_an_otlp_endpoint_is_configured()
    {
        await using var withEndpoint = BuildApp(endpoint: "http://localhost:4317");
        await using var withoutEndpoint = BuildApp(endpoint: string.Empty);

        withEndpoint.Services.GetService<TracerProvider>().ShouldNotBeNull();
        withEndpoint.Services.GetService<MeterProvider>().ShouldNotBeNull();
        withoutEndpoint.Services.GetService<TracerProvider>().ShouldBeNull();
        withoutEndpoint.Services.GetService<MeterProvider>().ShouldBeNull();
    }

    private static DefaultHttpContext ContextWithUser(Guid? userId)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(userId);
        return new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(currentUser).BuildServiceProvider() };
    }

    private static DefaultHttpContext ContextFor(string path, params string[] clientVersions)
    {
        var context = ContextWithUser(null);
        context.Request.Path = path;
        if (clientVersions.Length > 0)
        {
            context.Request.Headers[ObservabilityExtensions.ClientVersionHeader] = clientVersions;
        }

        return context;
    }

    private static WebApplication BuildApp(string endpoint)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        // An empty value also overrides an OTEL_EXPORTER_OTLP_ENDPOINT set in the developer's environment.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [ObservabilityExtensions.OtlpEndpointKey] = endpoint });
        builder.AddObservability();
        return builder.Build();
    }
}
