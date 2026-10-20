namespace TemplateName.Infrastructure.Common.Http;

/// <summary>
/// The names of the HTTP resilience pipelines, also the keys of their sections <c>Resilience:Pipelines:{name}</c>. Pass one to
/// <see cref="HttpClientBuilderExtensions.AddExternalHttpClient"/>.
/// </summary>
public static class ResiliencePipelines
{
    /// <summary>Attempt 10 s, total 30 s, 3 retries, exponential backoff with jitter, circuit breaker.</summary>
    public const string Default = "external-default";

    /// <summary>Like <see cref="Default"/> with 5 retries, for calls that matter more than the usual.</summary>
    public const string Critical = "external-critical";

    /// <summary>The breached-password lookup: attempt 1 s, one retry, total 2 s, circuit breaker.</summary>
    public const string Hibp = "hibp";
}
