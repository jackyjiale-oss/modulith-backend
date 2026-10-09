using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.UnitTests.Auth;

public sealed class HttpClientContextTests
{
    [Fact]
    public void Values_are_read_from_the_current_request()
    {
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace-1" };
        httpContext.Request.Headers.UserAgent = "Mozilla/5.0";
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.7");

        var client = ClientFor(httpContext);

        client.IpAddress.ShouldBe("203.0.113.7");
        client.UserAgent.ShouldBe("Mozilla/5.0");
        client.TraceId.ShouldBe(Activity.Current?.TraceId.ToHexString() ?? "trace-1");
    }

    [Fact]
    public void Over_long_values_are_cut_to_their_column_lengths()
    {
        // The longest IPv6 text with a zone id is 50 characters.
        var address = IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff%4294967295");
        var httpContext = new DefaultHttpContext { TraceIdentifier = new string('t', 100) };
        httpContext.Request.Headers.UserAgent = new string('u', 600);
        httpContext.Connection.RemoteIpAddress = address;
        var previous = Activity.Current;
        Activity.Current = null;

        try
        {
            var client = ClientFor(httpContext);

            address.ToString().Length.ShouldBeGreaterThan(AuthAuditLog.MaxIpAddressLength);
            client.IpAddress.ShouldBe(address.ToString()[..AuthAuditLog.MaxIpAddressLength]);
            client.UserAgent.ShouldBe(new string('u', AuthAuditLog.MaxUserAgentLength));
            client.TraceId.ShouldBe(new string('t', AuthAuditLog.MaxTraceIdLength));
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public void Values_are_null_outside_a_request()
    {
        var previous = Activity.Current;
        Activity.Current = null;

        try
        {
            var client = new HttpClientContext(new HttpContextAccessor());

            client.IpAddress.ShouldBeNull();
            client.UserAgent.ShouldBeNull();
            client.TraceId.ShouldBeNull();
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    private static HttpClientContext ClientFor(HttpContext httpContext)
        => new(new HttpContextAccessor { HttpContext = httpContext });
}
