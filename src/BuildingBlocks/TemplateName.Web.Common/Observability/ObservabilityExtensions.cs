using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
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
    private const string UrlQueryTag = "url.query";
    // The building blocks' own meter (the recurring-job scheduler in Infrastructure.Common, which this project does not reference) and
    // Polly's, through which the HTTP resilience pipelines report.
    private const string PlatformMeterName = "TemplateName.Platform";
    private const string PollyMeterName = "Polly";
    private const string DevelopmentOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Replaces the default logging with Serilog (levels from the <c>Serilog</c> section, sensitive values masked, <c>access_token</c>
    /// query values masked in every event, and any <c>ILogEventSink</c> or enricher registered as a service added) and keeps an
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
            services,
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

    /// <summary>
    /// Logs one structured event per request (health checks at <c>Verbose</c>) with the current <c>UserId</c> attached. Its
    /// <c>RequestPath</c> is the path alone: the query string, where a SignalR hub ticket travels as <c>access_token</c>, is never part
    /// of it (Serilog's <c>IncludeQueryInRequestPath</c> stays off).
    /// </summary>
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
        IServiceProvider services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string? otlpEndpoint)
    {
        loggerConfiguration
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.With<AccessTokenRedactionEnricher>()
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

    /// <summary>
    /// The ASP.NET Core trace options: health probes are not traced, and the <c>url.query</c> tag the instrumentation wrote gets any
    /// <c>access_token</c> value masked (<see cref="AccessTokenRedaction"/>; a SignalR client that cannot send headers puts its hub ticket
    /// there). The instrumentation already masks every query value by default (<c>?id=Redacted&amp;access_token=Redacted</c>); working
    /// on its tag keeps that, and keeps the credential out even when the default is switched off
    /// (<c>OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION</c>).
    /// </summary>
    internal static void ConfigureAspNetCoreTracing(AspNetCoreTraceInstrumentationOptions options)
    {
        options.Filter = httpContext => !httpContext.Request.Path.StartsWithSegments("/health");
        options.EnrichWithHttpRequest = (activity, _) =>
        {
            if (activity.GetTagItem(UrlQueryTag) is string query && AccessTokenRedaction.ContainsAccessToken(query))
            {
                activity.SetTag(UrlQueryTag, AccessTokenRedaction.Redact(query));
            }
        };
    }

    private static void AddOpenTelemetry(IServiceCollection services, string applicationName)
        => services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(applicationName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(ConfigureAspNetCoreTracing)
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation()
                .AddEntityFrameworkCoreInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(PlatformMeterName, PollyMeterName))
            .UseOtlpExporter();
}
