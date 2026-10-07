using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using TemplateName.Web.Common.Middleware;

namespace TemplateName.UnitTests.Web;

public sealed class MiddlewareTests
{
    [Fact]
    public async Task Security_headers_are_set_when_the_response_starts()
    {
        var context = new DefaultHttpContext();
        var response = RecordingHttpResponseFeature.Attach(context);
        var sut = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context);
        await response.FireOnStartingAsync();

        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
        headers["X-Frame-Options"].ToString().ShouldBe("DENY");
        headers["Content-Security-Policy"].ToString().ShouldBe("frame-ancestors 'none'");
        headers["Permissions-Policy"].ToString().ShouldBe("camera=(), microphone=(), geolocation=()");
    }

    [Fact]
    public async Task Security_headers_are_registered_before_the_pipeline_continues()
    {
        var context = new DefaultHttpContext();
        var response = RecordingHttpResponseFeature.Attach(context);
        var callbackCountSeenByNext = -1;
        var sut = new SecurityHeadersMiddleware(_ =>
        {
            callbackCountSeenByNext = response.CallbackCount;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context);

        callbackCountSeenByNext.ShouldBe(1);
    }

    [Fact]
    public async Task Trace_id_header_matches_current_activity()
    {
        using var activity = new Activity("test").Start();
        var context = new DefaultHttpContext();
        var response = RecordingHttpResponseFeature.Attach(context);
        var callbackCountSeenByNext = -1;
        var sut = new TraceIdHeaderMiddleware(_ =>
        {
            callbackCountSeenByNext = response.CallbackCount;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context);
        await response.FireOnStartingAsync();

        callbackCountSeenByNext.ShouldBe(1);
        context.Response.Headers["X-Trace-Id"].ToString().ShouldBe(activity.TraceId.ToHexString());
    }

    [Fact]
    public async Task Trace_id_header_falls_back_to_trace_identifier_without_activity()
    {
        Activity.Current = null;
        var context = new DefaultHttpContext { TraceIdentifier = "fallback-id" };
        var response = RecordingHttpResponseFeature.Attach(context);
        var sut = new TraceIdHeaderMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context);
        await response.FireOnStartingAsync();

        context.Response.Headers["X-Trace-Id"].ToString().ShouldBe("fallback-id");
    }
}
