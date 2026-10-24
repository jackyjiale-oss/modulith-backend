using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using TemplateName.Infrastructure.Common.Http;

namespace TemplateName.UnitTests.Infrastructure.Http;

public sealed class ResiliencePipelineTests
{
    private const string Url = "https://external.test/resource";

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Default_pipeline_retries_three_times_on_503_then_returns_the_last_response()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        using var host = ResilienceTestHost.Create(ResiliencePipelines.Default, handler);

        using var response = await host.RunAsync(host.Client.GetAsync(Url, TestContext.Current.CancellationToken), OneSecond);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        handler.Calls.ShouldBe(4);
    }

    [Fact]
    public async Task Critical_pipeline_retries_five_times()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        using var host = ResilienceTestHost.Create(ResiliencePipelines.Critical, handler);

        // The shipped defaults: the 90 s total holds the five jittered delays (23 s to 46 s), so all five retries are made.
        using var response = await host.RunAsync(host.Client.GetAsync(Url, TestContext.Current.CancellationToken), OneSecond);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        handler.Calls.ShouldBe(6);
    }

    [Fact]
    public async Task A_GET_is_retried_and_a_POST_is_not_by_default()
    {
        var getHandler = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        using var getHost = ResilienceTestHost.Create(ResiliencePipelines.Default, getHandler);
        using var getResponse = await getHost.RunAsync(getHost.Client.GetAsync(Url, TestContext.Current.CancellationToken), OneSecond);
        getHandler.Calls.ShouldBe(4);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
            using var host = ResilienceTestHost.Create(ResiliencePipelines.Default, handler);
            using var request = new HttpRequestMessage(method, Url);

            using var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            handler.Calls.ShouldBe(1, method.Method);
        }
    }

    [Fact]
    public async Task A_POST_is_retried_when_RetryUnsafeMethods_is_set()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        using var host = ResilienceTestHost.Create(
            ResiliencePipelines.Default,
            handler,
            new Dictionary<string, string?> { ["Resilience:Pipelines:external-default:RetryUnsafeMethods"] = "true" });
        using var content = new StringContent("{}");

        using var response = await host.RunAsync(host.Client.PostAsync(Url, content, TestContext.Current.CancellationToken), OneSecond);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        handler.Calls.ShouldBe(4);
    }

    [Fact]
    public async Task The_breaker_still_counts_the_failures_of_a_POST()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        using var host = ResilienceTestHost.Create(ResiliencePipelines.Default, handler);

        for (var call = 0; call < 10; call++)
        {
            using var content = new StringContent("{}");
            using var response = await host.Client.PostAsync(Url, content, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }

        handler.Calls.ShouldBe(10);
        using var next = new StringContent("{}");
        await Should.ThrowAsync<BrokenCircuitException>(() => host.Client.PostAsync(Url, next, TestContext.Current.CancellationToken));
        handler.Calls.ShouldBe(10);
    }

    [Fact]
    public async Task An_invalid_pipeline_value_stops_the_host_on_start_naming_the_key()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resilience:Pipelines:external-default:AttemptTimeout"] = "00:01:00",
        });
        builder.Services.AddExternalHttpClient("fake", ResiliencePipelines.Default);
        using var host = builder.Build();

        var exception = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("Resilience:Pipelines:external-default:AttemptTimeout");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_transient_status_is_retried(HttpStatusCode status)
    {
        var handler = new ScriptedHttpMessageHandler((call, _) => Task.FromResult(new HttpResponseMessage(call == 1 ? status : HttpStatusCode.OK)));
        using var host = ResilienceTestHost.Create(ResiliencePipelines.Default, handler);

        using var response = await host.RunAsync(host.Client.GetAsync(Url, TestContext.Current.CancellationToken), OneSecond);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_network_error_is_retried()
    {
        var handler = new ScriptedHttpMessageHandler((call, _) => call == 1
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var host = ResilienceTestHost.Create(ResiliencePipelines.Default, handler);

        using var response = await host.RunAsync(host.Client.GetAsync(Url, TestContext.Current.CancellationToken), OneSecond);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_4xx_is_not_retried()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.NotFound);
        using var host = ResilienceTestHost.Create(ResiliencePipelines.Default, handler);

        using var response = await host.Client.GetAsync(Url, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        handler.Calls.ShouldBe(1);

        // Nor does it count against the breaker: far more than its minimum throughput of 4xx answers still reach the handler.
        for (var call = 0; call < 30; call++)
        {
            using var next = await host.Client.GetAsync(Url, TestContext.Current.CancellationToken);
            next.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        handler.Calls.ShouldBe(31);
    }

    [Fact]
    public async Task Attempt_timeout_cancels_a_hung_attempt_after_ten_seconds()
    {
        var handler = ScriptedHttpMessageHandler.Hanging();
        using var host = ResilienceTestHost.Create(ResiliencePipelines.Default, handler, NoRetries());

        var call = host.Client.GetAsync(Url, TestContext.Current.CancellationToken);
        await ResilienceTestHost.WaitForCallsAsync(handler, 1);

        host.Time.Advance(TimeSpan.FromSeconds(9.9));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        call.IsCompleted.ShouldBeFalse();

        host.Time.Advance(TimeSpan.FromSeconds(0.2));
        await Should.ThrowAsync<TimeoutRejectedException>(() => call.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Total_timeout_bounds_all_attempts_at_thirty_seconds()
    {
        var handler = ScriptedHttpMessageHandler.Hanging();
        using var host = ResilienceTestHost.Create(ResiliencePipelines.Default, handler);

        var call = host.Client.GetAsync(Url, TestContext.Current.CancellationToken);
        await ResilienceTestHost.WaitForCallsAsync(handler, 1);

        // Three attempts of 10 s fill the 30 s, so a fourth never starts; the total stops the retrying whatever the delays were.
        await host.AdvanceAsync(TimeSpan.FromSeconds(29), OneSecond);
        call.IsCompleted.ShouldBeFalse();

        await host.AdvanceAsync(TimeSpan.FromSeconds(2), OneSecond);
        await Should.ThrowAsync<TimeoutRejectedException>(() => call.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        handler.Calls.ShouldBeLessThanOrEqualTo(3);
        handler.Calls.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task Breaker_opens_after_the_threshold_and_the_next_call_fails_fast_without_reaching_the_handler()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        using var host = ResilienceTestHost.Create(ResiliencePipelines.Default, handler, NoRetries());

        for (var call = 0; call < 10; call++)
        {
            using var response = await host.Client.GetAsync(Url, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }

        handler.Calls.ShouldBe(10);

        await Should.ThrowAsync<BrokenCircuitException>(() => host.Client.GetAsync(Url, TestContext.Current.CancellationToken));
        handler.Calls.ShouldBe(10);
    }

    [Fact]
    public async Task Breaker_half_opens_after_BreakDuration()
    {
        var healthy = false;
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)));
        using var host = ResilienceTestHost.Create(ResiliencePipelines.Default, handler, NoRetries());
        for (var call = 0; call < 10; call++)
        {
            using var response = await host.Client.GetAsync(Url, TestContext.Current.CancellationToken);
        }

        await Should.ThrowAsync<BrokenCircuitException>(() => host.Client.GetAsync(Url, TestContext.Current.CancellationToken));

        host.Time.Advance(TimeSpan.FromSeconds(29));
        await Should.ThrowAsync<BrokenCircuitException>(() => host.Client.GetAsync(Url, TestContext.Current.CancellationToken));
        handler.Calls.ShouldBe(10);

        // After the 30 s break the next call is the probe; it succeeds and closes the breaker.
        host.Time.Advance(TimeSpan.FromSeconds(2));
        healthy = true;
        using var probe = await host.Client.GetAsync(Url, TestContext.Current.CancellationToken);
        probe.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.Calls.ShouldBe(11);

        using var after = await host.Client.GetAsync(Url, TestContext.Current.CancellationToken);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.Calls.ShouldBe(12);
    }

    [Fact]
    public async Task Two_clients_on_one_pipeline_keep_a_breaker_each()
    {
        var failing = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        var healthy = ScriptedHttpMessageHandler.Always(HttpStatusCode.OK);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(NoRetries()).Build());
        services.AddExternalHttpClient("first", ResiliencePipelines.Default).ConfigurePrimaryHttpMessageHandler(() => failing);
        services.AddExternalHttpClient("second", ResiliencePipelines.Default).ConfigurePrimaryHttpMessageHandler(() => healthy);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        for (var call = 0; call < 10; call++)
        {
            using var response = await factory.CreateClient("first").GetAsync(Url, TestContext.Current.CancellationToken);
        }

        await Should.ThrowAsync<BrokenCircuitException>(() => factory.CreateClient("first").GetAsync(Url, TestContext.Current.CancellationToken));
        using var other = await factory.CreateClient("second").GetAsync(Url, TestContext.Current.CancellationToken);
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Options_validators_reject_attempt_above_total_zero_timeout_and_negative_retries()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.OK);

        OptionsFailureFor(handler, "AttemptTimeout", "00:00:31").ShouldContain("AttemptTimeout");
        OptionsFailureFor(handler, "TotalTimeout", "00:00:00").ShouldContain("TotalTimeout");
        OptionsFailureFor(handler, "AttemptTimeout", "00:00:00").ShouldContain("AttemptTimeout");
        OptionsFailureFor(handler, "Retries", "-1").ShouldContain("Retries");
        OptionsFailureFor(handler, "Retries", "11").ShouldContain("Retries");
        OptionsFailureFor(handler, "BaseDelay", "00:00:00").ShouldContain("BaseDelay");
        OptionsFailureFor(handler, "Breaker:FailureRatio", "0").ShouldContain("FailureRatio");
        OptionsFailureFor(handler, "Breaker:FailureRatio", "1.5").ShouldContain("FailureRatio");
        OptionsFailureFor(handler, "Breaker:MinimumThroughput", "1").ShouldContain("MinimumThroughput");
        OptionsFailureFor(handler, "Breaker:SamplingDuration", "00:00:00").ShouldContain("SamplingDuration");
        OptionsFailureFor(handler, "Breaker:BreakDuration", "00:00:00").ShouldContain("BreakDuration");
    }

    [Fact]
    public void A_pipeline_with_valid_edge_values_passes_validation()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.OK);
        using var host = ResilienceTestHost.Create(
            ResiliencePipelines.Default,
            handler,
            new Dictionary<string, string?>
            {
                ["Resilience:Pipelines:external-default:AttemptTimeout"] = "00:00:30",
                ["Resilience:Pipelines:external-default:Retries"] = "0",
                ["Resilience:Pipelines:external-default:Breaker:FailureRatio"] = "1",
            });

        var options = host.Services.GetRequiredService<IOptionsMonitor<ResilienceOptions>>().Get(ResiliencePipelines.Default);

        options.AttemptTimeout.ShouldBe(options.TotalTimeout);
        options.Retries.ShouldBe(0);
        options.Breaker.FailureRatio.ShouldBe(1);
    }

    [Fact]
    public async Task Config_overrides_a_pipeline_value()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        using var host = ResilienceTestHost.Create(
            ResiliencePipelines.Default,
            handler,
            new Dictionary<string, string?> { ["Resilience:Pipelines:external-default:Retries"] = "1" });

        using var response = await host.RunAsync(host.Client.GetAsync(Url, TestContext.Current.CancellationToken), OneSecond);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        handler.Calls.ShouldBe(2);

        // The other values keep their defaults.
        var options = host.Services.GetRequiredService<IOptionsMonitor<ResilienceOptions>>().Get(ResiliencePipelines.Default);
        options.Retries.ShouldBe(1);
        options.TotalTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.AttemptTimeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(ResiliencePipelines.Default, 3, 10, 30)]
    [InlineData(ResiliencePipelines.Critical, 5, 10, 90)]
    [InlineData(ResiliencePipelines.Hibp, 1, 1, 2)]
    public void Each_named_pipeline_has_its_defaults_and_the_breaker_defaults(string pipeline, int retries, int attemptSeconds, int totalSeconds)
    {
        using var host = ResilienceTestHost.Create(pipeline, ScriptedHttpMessageHandler.Always(HttpStatusCode.OK));

        var options = host.Services.GetRequiredService<IOptionsMonitor<ResilienceOptions>>().Get(pipeline);

        options.Retries.ShouldBe(retries);
        options.AttemptTimeout.ShouldBe(TimeSpan.FromSeconds(attemptSeconds));
        options.TotalTimeout.ShouldBe(TimeSpan.FromSeconds(totalSeconds));
        options.Breaker.FailureRatio.ShouldBe(0.5);
        options.RetryUnsafeMethods.ShouldBeFalse();
        options.Breaker.MinimumThroughput.ShouldBe(10);
        options.Breaker.SamplingDuration.ShouldBe(TimeSpan.FromSeconds(30));
        options.Breaker.BreakDuration.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_custom_pipeline_name_starts_from_the_default_values_and_reads_its_own_section()
    {
        using var host = ResilienceTestHost.Create(
            "sms-provider",
            ScriptedHttpMessageHandler.Always(HttpStatusCode.OK),
            new Dictionary<string, string?> { ["Resilience:Pipelines:sms-provider:Retries"] = "2" });

        var options = host.Services.GetRequiredService<IOptionsMonitor<ResilienceOptions>>().Get("sms-provider");

        options.Retries.ShouldBe(2);
        options.TotalTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    private static Dictionary<string, string?> NoRetries() => new() { ["Resilience:Pipelines:external-default:Retries"] = "0" };

    private static string OptionsFailureFor(ScriptedHttpMessageHandler handler, string key, string value)
    {
        using var host = ResilienceTestHost.Create(
            ResiliencePipelines.Default,
            handler,
            new Dictionary<string, string?> { [$"Resilience:Pipelines:external-default:{key}"] = value });

        var exception = Should.Throw<OptionsValidationException>(
            () => host.Services.GetRequiredService<IOptionsMonitor<ResilienceOptions>>().Get(ResiliencePipelines.Default));

        exception.OptionsType.ShouldBe(typeof(ResilienceOptions));
        return exception.Message;
    }
}
