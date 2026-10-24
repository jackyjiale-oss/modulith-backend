using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace TemplateName.Web.Common.Security;

public static class HttpSecurityExtensions
{
    internal const string RateLimitExceededCode = "rate_limit.exceeded";

    /// <summary>The policy name the global limiter passes to <see cref="IFixedWindowLimiterFactory"/> (a Redis key segment, for instance).</summary>
    internal const string GlobalLimiterPolicy = "global";

    // English default; CustomizeProblemDetails replaces it with the CommonErrorMessages entry for the caller's language.
    private const string RateLimitExceededDetail = "Too many requests. Try again later.";

    /// <summary>
    /// Registers CORS (listed origins only), the global rate limiter, the <see cref="RateLimitPolicies.AuthStrict"/> policy and
    /// forwarded-headers handling. Options are bound from the <c>Cors</c>, <c>RateLimiting</c> and <c>ForwardedHeaders</c> sections,
    /// validated on start and read lazily. Both limiters get each partition's fixed window from <see cref="IFixedWindowLimiterFactory"/>,
    /// which is registered here as the in-memory default unless one is registered already (<c>AddPlatformRedis</c> replaces it either way).
    /// </summary>
    public static IServiceCollection AddHttpSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiCorsOptions>().Bind(configuration.GetSection(ApiCorsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<RateLimitingOptions>().Bind(configuration.GetSection(RateLimitingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ApiForwardedHeadersOptions>().Bind(configuration.GetSection(ApiForwardedHeadersOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IOptions<ApiCorsOptions>>(
            (corsOptions, apiCorsOptions) => corsOptions.AddDefaultPolicy(policy => policy
                .WithOrigins(apiCorsOptions.Value.AllowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()

                // Response headers browser code needs that are not CORS-safelisted.
                .WithExposedHeaders("X-Trace-Id", "Location", "Retry-After", "Idempotency-Replayed")));

        services.TryAddSingleton<IFixedWindowLimiterFactory, InMemoryFixedWindowLimiterFactory>();
        services.AddRateLimiter(options => options.OnRejected = WriteRateLimitProblemAsync);
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitingOptions>, IFixedWindowLimiterFactory>(
            (limiterOptions, rateLimitingOptions, limiterFactory) =>
            {
                limiterOptions.GlobalLimiter = CreateGlobalLimiter(rateLimitingOptions.Value, limiterFactory);
                limiterOptions.AddPolicy(
                    RateLimitPolicies.AuthStrict,
                    httpContext => CreateAuthStrictPartition(httpContext, rateLimitingOptions.Value, limiterFactory));
            });

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ApiForwardedHeadersOptions>>(
            (forwardedHeadersOptions, apiOptions) => ConfigureForwardedHeaders(forwardedHeadersOptions, apiOptions.Value));

        return services;
    }

    private static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(RateLimitingOptions options, IFixedWindowLimiterFactory limiterFactory)
        => PartitionedRateLimiter.Create<HttpContext, string>(httpContext => RateLimitPartition.Get(
            ResolvePartitionKey(httpContext),
            partitionKey => limiterFactory.Create(GlobalLimiterPolicy, partitionKey, options.GlobalPermitLimit, options.GlobalWindow)));

    /// <summary>A fixed window per client address, whoever is signed in: the credential endpoints it guards are anonymous.</summary>
    private static RateLimitPartition<string> CreateAuthStrictPartition(
        HttpContext httpContext,
        RateLimitingOptions options,
        IFixedWindowLimiterFactory limiterFactory)
        => RateLimitPartition.Get(
            ClientAddressPartitionKey(httpContext),
            partitionKey => limiterFactory.Create(RateLimitPolicies.AuthStrict, partitionKey, options.AuthStrictPermitLimit, options.AuthStrictWindow));

    /// <summary><c>user:{sub}</c> for an authenticated caller, else <see cref="ClientAddressPartitionKey"/>.</summary>
    private static string ResolvePartitionKey(HttpContext httpContext)
    {
        var user = httpContext.User;
        var subject = user.Identity?.IsAuthenticated == true ? user.FindFirst("sub")?.Value : null;
        return subject is not null ? $"user:{subject}" : ClientAddressPartitionKey(httpContext);
    }

    /// <summary>
    /// <c>ip:{address}</c>: the connection's remote address as left by the forwarded-headers middleware, which rewrites it only for a
    /// trusted proxy, so a spoofed <c>X-Forwarded-For</c> cannot pick a partition.
    /// </summary>
    private static string ClientAddressPartitionKey(HttpContext httpContext)
        => $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    private static async ValueTask WriteRateLimitProblemAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        _ = await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Detail = RateLimitExceededDetail,
                Extensions = { ["code"] = RateLimitExceededCode },
            },
        });
    }

    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, ApiForwardedHeadersOptions apiOptions)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // The defaults trust loopback only. Replace them when proxies are configured; keep them when nothing is.
        if (apiOptions.KnownProxies.Length > 0 || apiOptions.KnownNetworks.Length > 0)
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
        }

        foreach (var proxy in apiOptions.KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        foreach (var network in apiOptions.KnownNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    }
}
