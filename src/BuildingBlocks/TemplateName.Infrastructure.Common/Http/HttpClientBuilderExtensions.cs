using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace TemplateName.Infrastructure.Common.Http;

public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Registers the named <see cref="HttpClient"/> <paramref name="name"/> behind a resilience pipeline: the total timeout, then
    /// retries with exponential backoff and jitter, then a circuit breaker, then the timeout of one attempt. Its settings are
    /// <c>Resilience:Pipelines:{pipeline}</c> (defaults and ranges in <c>docs/building-blocks/infrastructure-common.md</c>), validated on
    /// start; two clients may share a pipeline's settings. The pipeline retries 5xx, 408, 429, network errors and its own attempt
    /// timeout (but a POST, PUT, PATCH, DELETE or CONNECT only when <c>RetryUnsafeMethods</c> is set), and any other 4xx answer is
    /// neither retried nor held against the breaker. An open breaker throws
    /// <c>Polly.CircuitBreaker.BrokenCircuitException</c> and a timeout <c>Polly.Timeout.TimeoutRejectedException</c>, so a caller that
    /// must degrade handles both (the Auth module's breached-password check does). The pipeline measures time with the registered
    /// <see cref="TimeProvider"/>; the client has no <see cref="HttpClient.Timeout"/> of its own, the total timeout is it.
    /// </summary>
    /// <param name="services">The service collection; an <c>IConfiguration</c> must be registered, as it is in a host.</param>
    /// <param name="name">The name of the client, as given to <see cref="IHttpClientFactory.CreateClient(string)"/>.</param>
    /// <param name="pipeline">One of <see cref="ResiliencePipelines"/> or the name of a pipeline of your own (it starts from the default values).</param>
    public static IHttpClientBuilder AddExternalHttpClient(this IServiceCollection services, string name, string pipeline = ResiliencePipelines.Default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeline);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<ResilienceOptions>, ResilienceOptionsSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ResilienceOptions>, ResilienceOptionsValidator>());
        services.AddOptions<ResilienceOptions>(pipeline).ValidateOnStart();

        var builder = services.AddHttpClient(name, client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.AddResilienceHandler(pipeline, (pipelineBuilder, context) =>
            Configure(pipelineBuilder, pipeline, context.ServiceProvider.GetRequiredService<IOptionsMonitor<ResilienceOptions>>().Get(pipeline)));
        return builder;
    }

    private static void Configure(ResiliencePipelineBuilder<HttpResponseMessage> builder, string pipeline, ResilienceOptions options)
    {
        // Outermost first. The retry and the breaker keep the standard predicate of Microsoft.Extensions.Http.Resilience (5xx, 408,
        // 429, HttpRequestException and the timeout of an attempt), so any other 4xx is not a failure.
        builder.AddTimeout(new HttpTimeoutStrategyOptions
        {
            Name = $"{pipeline}-total-timeout",
            Timeout = options.TotalTimeout,
        });

        // Polly's retry needs at least one attempt, so no retries means no retry strategy.
        if (options.Retries > 0)
        {
            var retry = new HttpRetryStrategyOptions
            {
                Name = $"{pipeline}-retry",
                MaxRetryAttempts = options.Retries,
                Delay = options.BaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
            };

            // A POST, PUT, PATCH, DELETE or CONNECT is not repeated unless the pipeline says so: a blind retry can send twice.
            if (!options.RetryUnsafeMethods)
            {
                retry.DisableForUnsafeHttpMethods();
            }

            builder.AddRetry(retry);
        }

        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            Name = $"{pipeline}-breaker",
            FailureRatio = options.Breaker.FailureRatio,
            MinimumThroughput = options.Breaker.MinimumThroughput,
            SamplingDuration = options.Breaker.SamplingDuration,
            BreakDuration = options.Breaker.BreakDuration,
        });

        builder.AddTimeout(new HttpTimeoutStrategyOptions
        {
            Name = $"{pipeline}-attempt-timeout",
            Timeout = options.AttemptTimeout,
        });
    }
}
