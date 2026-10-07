using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using TemplateName.Web.Common.Middleware;

namespace TemplateName.UnitTests.Web;

public sealed class MiddlewareTests
{
    [Fact]
    public async Task Security_headers_are_set()
    {
        var context = new DefaultHttpContext();
        var sut = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context);

        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
        headers["X-Frame-Options"].ToString().ShouldBe("DENY");
        headers["Content-Security-Policy"].ToString().ShouldBe("frame-ancestors 'none'");
        headers["Permissions-Policy"].ToString().ShouldBe("camera=(), microphone=(), geolocation=()");
    }

    [Fact]
    public async Task Security_headers_are_set_before_the_pipeline_continues()
    {
        var context = new DefaultHttpContext();
        var wasSetBeforeNext = false;
        var sut = new SecurityHeadersMiddleware(httpContext =>
        {
            wasSetBeforeNext = httpContext.Response.Headers.ContainsKey("X-Content-Type-Options");
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context);

        wasSetBeforeNext.ShouldBeTrue();
    }

    [Fact]
    public async Task Trace_id_header_matches_current_activity()
    {
        using var activity = new Activity("test").Start();
        var context = new DefaultHttpContext();
        string? headerSeenByNext = null;
        var sut = new TraceIdHeaderMiddleware(httpContext =>
        {
            headerSeenByNext = httpContext.Response.Headers["X-Trace-Id"];
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context);

        context.Response.Headers["X-Trace-Id"].ToString().ShouldBe(activity.TraceId.ToHexString());
        headerSeenByNext.ShouldBe(activity.TraceId.ToHexString());
    }

    [Fact]
    public async Task Trace_id_header_falls_back_to_trace_identifier_without_activity()
    {
        Activity.Current = null;
        var context = new DefaultHttpContext { TraceIdentifier = "fallback-id" };
        var sut = new TraceIdHeaderMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context);

        context.Response.Headers["X-Trace-Id"].ToString().ShouldBe("fallback-id");
    }
}
