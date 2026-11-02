using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using Serilog.Events;
using Serilog.Parsing;
using TemplateName.Web.Common.Observability;

namespace TemplateName.UnitTests.Web;

public sealed class AccessTokenRedactionTests
{
    private const string Secret = "q1w2e3r4t5y6u7i8o9p0a1s2d3f4g5h6j7k8l9z0x1c";

    [Theory]
    [InlineData("?access_token=" + Secret, "?access_token=[Redacted]")]
    [InlineData("?id=abc&access_token=" + Secret, "?id=abc&access_token=[Redacted]")]
    [InlineData("?access_token=" + Secret + "&id=abc", "?access_token=[Redacted]&id=abc")]
    [InlineData("?ACCESS_TOKEN=" + Secret, "?ACCESS_TOKEN=[Redacted]")]
    [InlineData("?access%5Ftoken=" + Secret, "?access%5Ftoken=[Redacted]")]
    [InlineData("http://localhost/hubs/notifications?id=abc&access_token=" + Secret, "http://localhost/hubs/notifications?id=abc&access_token=[Redacted]")]
    [InlineData("?access_token=a&access_token=b", "?access_token=[Redacted]&access_token=[Redacted]")]
    [InlineData("?input_token=" + Secret, "?input_token=[Redacted]")]
    [InlineData("?INPUT%5FTOKEN=" + Secret, "?INPUT%5FTOKEN=[Redacted]")]
    [InlineData(
        "https://graph.facebook.com/v23.0/debug_token?input_token=" + Secret + "&access_token=123%7Capp-secret",
        "https://graph.facebook.com/v23.0/debug_token?input_token=[Redacted]&access_token=[Redacted]")]
    public void Access_token_values_are_redacted(string text, string expected)
    {
        AccessTokenRedaction.Redact(text).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?id=abc")]
    [InlineData("?my_access_token=abc")]
    [InlineData("?my_input_token=abc")]
    [InlineData("/hubs/notifications")]
    public void Text_without_an_access_token_is_unchanged(string text)
    {
        AccessTokenRedaction.Redact(text).ShouldBe(text);
    }

    [Fact]
    public void Enricher_redacts_every_string_property_that_carries_an_access_token()
    {
        // The shape of ASP.NET Core's "Request starting" event: the query string is its own property, rendered into the message.
        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(
            DateTimeOffset.UnixEpoch,
            LogEventLevel.Information,
            exception: null,
            parser.Parse("Request starting {Path}{QueryString}"),
            [
                new LogEventProperty("Path", new ScalarValue("/hubs/notifications")),
                new LogEventProperty("QueryString", new ScalarValue("?id=abc&access_token=" + Secret)),
                new LogEventProperty("Count", new ScalarValue(3)),
            ]);

        new AccessTokenRedactionEnricher().Enrich(logEvent, propertyFactory: null!);

        logEvent.RenderMessage().ShouldBe("Request starting \"/hubs/notifications\"\"?id=abc&access_token=[Redacted]\"");
        logEvent.RenderMessage().ShouldNotContain(Secret);
        ((ScalarValue)logEvent.Properties["Count"]).Value.ShouldBe(3);
    }

    [Theory]
    [InlineData("?id=abc&access_token=" + Secret, "?id=abc&access_token=[Redacted]")]
    [InlineData("?id=Redacted&access_token=Redacted", "?id=Redacted&access_token=[Redacted]")]
    [InlineData("?id=abc", "?id=abc")]
    public void Trace_enrichment_masks_the_access_token_in_the_url_query_tag(string tag, string expected)
    {
        // The tag is what the instrumentation wrote: the raw query with its redaction switched off, else every value masked already.
        var options = new AspNetCoreTraceInstrumentationOptions();
        ObservabilityExtensions.ConfigureAspNetCoreTracing(options);
        using var activity = new Activity("request");
        activity.SetTag("url.query", tag);

        options.EnrichWithHttpRequest.ShouldNotBeNull();
        options.EnrichWithHttpRequest(activity, RequestWithQuery("?id=abc&access_token=" + Secret));

        activity.GetTagItem("url.query").ShouldBe(expected);
    }

    [Fact]
    public void Enricher_redacts_an_input_token()
    {
        // The shape of IHttpClientFactory's "Start processing HTTP request" event, were its own URI redaction switched off.
        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(
            DateTimeOffset.UnixEpoch,
            LogEventLevel.Information,
            exception: null,
            parser.Parse("Start processing HTTP request {HttpMethod} {Uri}"),
            [
                new LogEventProperty("HttpMethod", new ScalarValue("GET")),
                new LogEventProperty("Uri", new ScalarValue("https://graph.facebook.com/debug_token?input_token=" + Secret + "&access_token=1%7Cs")),
            ]);

        new AccessTokenRedactionEnricher().Enrich(logEvent, propertyFactory: null!);

        logEvent.RenderMessage().ShouldNotContain(Secret);
        logEvent.RenderMessage().ShouldContain("input_token=[Redacted]&access_token=[Redacted]");
    }

    [Theory]
    [InlineData(
        "https://graph.facebook.com/debug_token?input_token=" + Secret + "&access_token=1%7Cs",
        "https://graph.facebook.com/debug_token?input_token=[Redacted]&access_token=[Redacted]")]
    [InlineData(
        "https://graph.facebook.com/me?fields=Redacted&access_token=Redacted&appsecret_proof=Redacted",
        "https://graph.facebook.com/me?fields=Redacted&access_token=[Redacted]&appsecret_proof=Redacted")]
    [InlineData("https://accounts.google.com/.well-known/openid-configuration", "https://accounts.google.com/.well-known/openid-configuration")]
    public void Http_client_trace_enrichment_masks_the_tokens_in_the_url_full_tag(string tag, string expected)
    {
        // The tag is what the HttpClient instrumentation wrote: every query value already masked by default (1.20.1, unless
        // OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION is true), else the raw URL.
        var options = new HttpClientTraceInstrumentationOptions();
        ObservabilityExtensions.ConfigureHttpClientTracing(options);
        using var activity = new Activity("outgoing");
        activity.SetTag("url.full", tag);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(tag.Replace("[Redacted]", "x", StringComparison.Ordinal)));

        options.EnrichWithHttpRequestMessage.ShouldNotBeNull();
        options.EnrichWithHttpRequestMessage(activity, request);

        activity.GetTagItem("url.full").ShouldBe(expected);
    }

    [Fact]
    public void Http_client_trace_enrichment_adds_no_url_full_tag()
    {
        var options = new HttpClientTraceInstrumentationOptions();
        ObservabilityExtensions.ConfigureHttpClientTracing(options);
        using var activity = new Activity("outgoing");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.facebook.com/debug_token?input_token=" + Secret);

        options.EnrichWithHttpRequestMessage!(activity, request);

        activity.GetTagItem("url.full").ShouldBeNull();
    }

    [Fact]
    public void Trace_enrichment_adds_no_url_query_tag()
    {
        var options = new AspNetCoreTraceInstrumentationOptions();
        ObservabilityExtensions.ConfigureAspNetCoreTracing(options);
        using var activity = new Activity("request");

        options.EnrichWithHttpRequest!(activity, RequestWithQuery("?access_token=" + Secret));

        activity.GetTagItem("url.query").ShouldBeNull();
    }

    [Fact]
    public void Trace_filter_still_skips_health_probes()
    {
        var options = new AspNetCoreTraceInstrumentationOptions();
        ObservabilityExtensions.ConfigureAspNetCoreTracing(options);

        options.Filter.ShouldNotBeNull();
        options.Filter(ContextFor("/health/live")).ShouldBeFalse();
        options.Filter(ContextFor("/hubs/notifications")).ShouldBeTrue();
    }

    private static HttpRequest RequestWithQuery(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return context.Request;
    }

    private static DefaultHttpContext ContextFor(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context;
    }
}
