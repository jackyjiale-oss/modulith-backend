using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;
using TemplateName.Application.Common.Identity;

namespace TemplateName.Web.Common.Observability;

public static class ObservabilityExtensions
{
    internal const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    private const string OtlpProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string HttpProtobufProtocol = "http/protobuf";
    private const string LogsPath = "/v1/logs";
    private const string DevelopmentOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Replaces the default logging with Serilog (levels from the <c>Serilog</c> section, sensitive values masked) and keeps an
    /// activity alive for every request so error responses always carry a W3C trace id. When <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
    /// is set (environment variable or configuration), it also exports logs, traces and metrics over OTLP.
    /// </summary>
    /// <remarks>
    /// The endpoint decides which OpenTelemetry services are registered, so it is read once here, when the host is configured; it
    /// is not a value that <c>WebApplicationFactory</c> overrides can change. Serilog itself is configured lazily when the host builds.
    /// </remarks>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration[OtlpEndpointKey];
        var exportsTelemetry = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Services.AddSerilog((services, loggerConfiguration) => ConfigureSerilog(
            loggerConfiguration,
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IHostEnvironment>(),
            exportsTelemetry ? otlpEndpoint : null));

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RequestActivityBackgroundService>());

        if (exportsTelemetry)
        {
            AddOpenTelemetry(builder.Services, builder.Environment.ApplicationName);
        }

        return builder;
    }

    /// <summary>Logs one structured event per request (health checks at <c>Verbose</c>) with the current <c>UserId</c> attached.</summary>
    public static WebApplication UseRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = ResolveRequestLogLevel;
            options.EnrichDiagnosticContext = EnrichRequest;
        });

        return app;
    }

    internal static LogEventLevel ResolveRequestLogLevel(HttpContext httpContext, double elapsedMilliseconds, Exception? exception)
    {
        // Failures stay visible even on health endpoints; only healthy probe traffic is demoted.
        if (exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            return LogEventLevel.Error;
        }

        return httpContext.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose : LogEventLevel.Information;
    }

    internal static void EnrichRequest(IDiagnosticContext diagnosticContext, HttpContext httpContext)
    {
        var userId = httpContext.RequestServices.GetService<ICurrentUser>()?.UserId;
        if (userId is not null)
        {
            diagnosticContext.Set("UserId", userId);
        }
    }

    private static void ConfigureSerilog(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        IHostEnvironment environment,
        string? otlpEndpoint)
    {
        loggerConfiguration
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", environment.ApplicationName)
            .Enrich.WithProperty("Environment", environment.EnvironmentName)
            .Destructure.With<SensitiveDataDestructuringPolicy>();

        if (environment.IsDevelopment())
        {
            loggerConfiguration.WriteTo.Console(outputTemplate: DevelopmentOutputTemplate);
        }
        else
        {
            loggerConfiguration.WriteTo.Console(new RenderedCompactJsonFormatter());
        }

        if (otlpEndpoint is not null)
        {
            var protocol = configuration[OtlpProtocolKey];
            var usesHttp = string.Equals(protocol, HttpProtobufProtocol, StringComparison.OrdinalIgnoreCase);
            loggerConfiguration.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = usesHttp ? otlpEndpoint.TrimEnd('/') + LogsPath : otlpEndpoint;
                options.Protocol = usesHttp ? OtlpProtocol.HttpProtobuf : OtlpProtocol.Grpc;
                options.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = environment.ApplicationName };
            });
        }
    }

    private static void AddOpenTelemetry(IServiceCollection services, string applicationName)
        => services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(applicationName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options => options.Filter = httpContext => !httpContext.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation()
                .AddEntityFrameworkCoreInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .UseOtlpExporter();
}
