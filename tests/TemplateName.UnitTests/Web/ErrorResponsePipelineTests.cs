using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TemplateName.Web.Common;

namespace TemplateName.UnitTests.Web;

/// <summary>Runs the production middleware order against a real pipeline and checks the headers survive error handling.</summary>
public sealed partial class ErrorResponsePipelineTests
{
    private static readonly (string Name, string Value)[] ExpectedSecurityHeaders =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("Referrer-Policy", "no-referrer"),
        ("X-Frame-Options", "DENY"),
        ("Content-Security-Policy", "frame-ancestors 'none'"),
        ("Permissions-Policy", "camera=(), microphone=(), geolocation=()"),
    ];

    [Fact]
    public async Task Unhandled_exception_returns_500_with_matching_trace_id_and_security_headers()
    {
        using var response = await SendAsync(HttpMethod.Get, "/throw");

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "server.unexpected_error");
    }

    [Fact]
    public async Task Malformed_json_body_returns_400_request_malformed_with_matching_trace_id_and_security_headers()
    {
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        using var response = await SendAsync(HttpMethod.Post, "/echo", content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "request.malformed");
    }

    [Fact]
    public async Task Unknown_route_returns_404_with_matching_trace_id_and_security_headers()
    {
        using var response = await SendAsync(HttpMethod.Get, "/missing");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "http.404");
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);

        var traceIdHeader = response.Headers.GetValues("X-Trace-Id").ShouldHaveSingleItem();
        TraceIdPattern().IsMatch(traceIdHeader).ShouldBeTrue();

        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);
        body.RootElement.GetProperty("traceId").GetString().ShouldBe(traceIdHeader);
        body.RootElement.GetProperty("code").GetString().ShouldBe(code);

        foreach (var (name, value) in ExpectedSecurityHeaders)
        {
            response.Headers.GetValues(name).ShouldHaveSingleItem().ShouldBe(value);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        // Hosting only starts a request Activity when something listens (OpenTelemetry does in production).
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services => services.AddRouting().AddWebCommon())
                .Configure(app =>
                {
                    app.UseTraceIdHeader();
                    app.UseSecurityHeaders();
                    app.UseExceptionHandler();
                    app.UseStatusCodePages();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/throw", () => Task.FromException<IResult>(new InvalidOperationException("boom")));
                        endpoints.MapPost("/echo", (EchoRequest request) => Results.Ok(request));
                    });
                }))
            .StartAsync(TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(method, path) { Content = content };
        var response = await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        await response.Content.LoadIntoBufferAsync(TestContext.Current.CancellationToken);
        return response;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex TraceIdPattern();

    private sealed record EchoRequest(string Name);
}
