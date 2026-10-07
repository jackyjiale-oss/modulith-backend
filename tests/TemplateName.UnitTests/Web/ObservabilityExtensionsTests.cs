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
