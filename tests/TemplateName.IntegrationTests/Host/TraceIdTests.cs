using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Host;

public sealed class TraceIdTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Trace_id_is_a_w3c_trace_id_without_an_otlp_endpoint()
    {
        using var response = await Client.GetAsync("/api/v1/does-not-exist", Ct);

        await AssertTraceIdIsW3cAndEchoedInBodyAsync(response);
    }

    /// <summary>
    /// ASP.NET Core only starts a request Activity when something observes it. Hosting treats an enabled logger as an observer,
    /// so a silent logger factory leaves nothing listening unless the observability setup registers its own listener.
    /// </summary>
    [Fact]
    public async Task Trace_id_is_a_w3c_trace_id_when_logging_is_silent()
    {
        await using var silent = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)));
        using var client = silent.CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist", Ct);

        await AssertTraceIdIsW3cAndEchoedInBodyAsync(response);
    }

    private static async Task AssertTraceIdIsW3cAndEchoedInBodyAsync(HttpResponseMessage response)
    {
        var headerValue = response.Headers.GetValues("X-Trace-Id").Single();
        headerValue.ShouldMatch("^[0-9a-f]{32}$");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("traceId").GetString().ShouldBe(headerValue);
    }
}
