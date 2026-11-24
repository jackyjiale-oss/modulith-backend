using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly.CircuitBreaker;
using Polly.Timeout;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Sms;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Notifications.Sms;

public sealed class TwilioSmsTransportTests
{
    private const string AccountSid = "ACtest-account-sid";
    private const string AuthToken = "twilio-auth-token-secret";
    private const string FromNumber = "+15005550006";
    private const string ServiceSid = "MG0123456789abcdef0123456789abcdef";
    private const string ToNumber = "+60123456789";
    private const string Body = "Acme: your verification code is 042137. It expires in 10 minutes.";
    private const string MessageSid = "SM0123456789abcdef0123456789abcdef";

    private readonly StubHandler _handler = new();
    private readonly RecordingLogger<TwilioSmsTransport> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<HttpStatusCode, int?, string, bool> FailureTable => new()
    {
        // The recipient: nothing a retry can fix.
        { HttpStatusCode.BadRequest, 21211, "twilio_21211", true },
        { HttpStatusCode.BadRequest, 21614, "twilio_21614", true },
        { HttpStatusCode.BadRequest, 21408, "twilio_21408", true },
        { HttpStatusCode.BadRequest, 21610, "twilio_21610", true },

        // The server's own configuration: transient, so the deliveries wait for the fix instead of dead-lettering.
        { HttpStatusCode.Unauthorized, 20003, "twilio_20003", false },
        { HttpStatusCode.BadRequest, 21606, "twilio_21606", false },
        { HttpStatusCode.BadRequest, 21659, "twilio_21659", false },

        // The status decides when the code is none of the above.
        { HttpStatusCode.Unauthorized, null, "twilio_http_401", false },
        { HttpStatusCode.TooManyRequests, 20429, "twilio_http_429", false },
        { HttpStatusCode.InternalServerError, null, "twilio_http_500", false },
        { HttpStatusCode.ServiceUnavailable, null, "twilio_http_503", false },
        { HttpStatusCode.BadRequest, 21612, "twilio_http_400", true },
        { HttpStatusCode.NotFound, 20404, "twilio_http_404", true },
        { HttpStatusCode.Forbidden, null, "twilio_http_403", true },
    };

    [Fact]
    public async Task Sends_the_form_with_basic_auth()
    {
        await Transport(new TwilioOptions { AccountSid = AccountSid, AuthToken = AuthToken, From = FromNumber }).SendAsync(Message(), Ct);

        var request = _handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri($"https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json"));
        request.Authorization.ShouldBe($"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes($"{AccountSid}:{AuthToken}"))}");
        request.ContentType.ShouldBe("application/x-www-form-urlencoded");
        Form(request.Body).ShouldBe(
            new Dictionary<string, string> { ["To"] = ToNumber, ["Body"] = Body, ["From"] = FromNumber },
            ignoreOrder: true);
    }

    [Fact]
    public async Task Sends_with_a_messaging_service_instead_of_a_number()
    {
        await Transport(new TwilioOptions { AccountSid = AccountSid, AuthToken = AuthToken, MessagingServiceSid = ServiceSid }).SendAsync(Message(), Ct);

        Form(_handler.Requests.ShouldHaveSingleItem().Body).ShouldBe(
            new Dictionary<string, string> { ["To"] = ToNumber, ["Body"] = Body, ["MessagingServiceSid"] = ServiceSid },
            ignoreOrder: true);
    }

    [Fact]
    public async Task Returns_the_message_sid()
    {
        _handler.Respond(HttpStatusCode.Created, $$"""{"sid":"{{MessageSid}}","status":"queued","to":"{{ToNumber}}"}""");

        (await Transport().SendAsync(Message(), Ct)).ShouldBe(MessageSid);
    }

    [Theory]
    [InlineData("""{"status":"queued"}""")]
    [InlineData("""{"sid":"XX1"}""")]
    [InlineData("""{"sid":"SM<script>alert(1)</script>"}""")]
    [InlineData("""{"sid":"SM0123456789ABCDEF0123456789ABCDEF"}""")]
    [InlineData("""{"sid":42}""")]
    [InlineData("""[1]""")]
    [InlineData("not json")]
    public async Task A_missing_or_malformed_sid_returns_none(string body)
    {
        _handler.Respond(HttpStatusCode.Created, body);

        var sid = await Transport().SendAsync(Message(), Ct);

        sid.ShouldBeNull();
        _logger.Entries.ShouldContain(entry => entry.Message.Contains("no usable message SID", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(FailureTable))]
    public async Task Failures_map_to_reason_codes(HttpStatusCode status, int? code, string reason, bool permanent)
    {
        _handler.Respond(status, code is null ? """{"message":"x","status":0}""" : $$"""{"code":{{code}},"message":"x","more_info":"https://www.twilio.com/docs/errors/{{code}}","status":{{(int)status}}}""");

        var failure = await Should.ThrowAsync<SmsSendException>(() => Transport().SendAsync(Message(), Ct));

        failure.Reason.ShouldBe(reason);
        failure.IsPermanent.ShouldBe(permanent);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "<html><body>Bad gateway</body></html>", "twilio_http_502", false)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"21211","message":"x"}""", "twilio_http_400", true)]
    [InlineData(HttpStatusCode.BadRequest, """[1]""", "twilio_http_400", true)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":21211.5}""", "twilio_http_400", true)]
    public async Task An_unexpected_error_body_falls_back_to_the_status(HttpStatusCode status, string body, string reason, bool permanent)
    {
        _handler.Respond(status, body, body.StartsWith('<') ? "text/html" : "application/json");

        var failure = await Should.ThrowAsync<SmsSendException>(() => Transport().SendAsync(Message(), Ct));

        failure.Reason.ShouldBe(reason);
        failure.IsPermanent.ShouldBe(permanent);
    }

    [Fact]
    public async Task A_connection_failure_is_transient()
    {
        _handler.Throw(new HttpRequestException("connection refused"));

        var failure = await Should.ThrowAsync<SmsSendException>(() => Transport().SendAsync(Message(), Ct));

        failure.Reason.ShouldBe("connection_failed");
        failure.IsPermanent.ShouldBeFalse();
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("pipeline-timeout")]
    [InlineData("circuit")]
    public async Task A_timeout_or_an_open_breaker_is_transient(string kind)
    {
        _handler.Throw(kind switch
        {
            "timeout" => new TaskCanceledException("timed out"),
            "pipeline-timeout" => new TimeoutRejectedException("timed out"),
            _ => new BrokenCircuitException("open"),
        });

        var failure = await Should.ThrowAsync<SmsSendException>(() => Transport().SendAsync(Message(), Ct));

        failure.Reason.ShouldBe(kind == "circuit" ? "circuit_open" : "timeout");
        failure.IsPermanent.ShouldBeFalse();
    }

    [Fact]
    public async Task The_number_and_body_never_appear_in_logs_or_exceptions()
    {
        _handler.Respond(HttpStatusCode.BadRequest, $$"""{"code":21211,"message":"The 'To' number {{ToNumber}} is not a valid phone number."}""");

        var failure = await Should.ThrowAsync<SmsSendException>(() => Transport().SendAsync(Message(), Ct));
        _handler.Respond(HttpStatusCode.Created, $$"""{"sid":"{{MessageSid}}"}""");
        await Transport().SendAsync(Message(), Ct);

        failure.Message.ShouldNotContain("60123456789");
        failure.InnerException.ShouldBeNull();
        Message().ToString().ShouldNotContain("60123456789");
        Message().ToString().ShouldNotContain("042137");
        _logger.Entries.ShouldNotBeEmpty();
        _logger.Entries.ShouldAllBe(entry =>
            !entry.Message.Contains("60123456789") && !entry.Message.Contains("042137") && !entry.Message.Contains(AuthToken));
        _logger.Exceptions.ShouldBeEmpty();
    }

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(pair => pair.Split('=')).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));

    private static SmsMessage Message() => new(Guid.NewGuid(), ToNumber, Body);

    private TwilioSmsTransport Transport(TwilioOptions? options = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(TwilioSmsTransport.ClientName).Returns(_ => new HttpClient(_handler, disposeHandler: false));
        return new TwilioSmsTransport(
            factory,
            Options.Create(options ?? new TwilioOptions { AccountSid = AccountSid, AuthToken = AuthToken, From = FromNumber }),
            _logger);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.Created;
        private string _body = """{"sid":"SM0123456789abcdef0123456789abcdef"}""";
        private string _mediaType = "application/json";
        private Exception? _exception;

        public List<(HttpMethod Method, Uri? Uri, string? Authorization, string? ContentType, string Body)> Requests { get; } = [];

        public void Respond(HttpStatusCode status, string body, string mediaType = "application/json")
        {
            (_status, _body, _mediaType, _exception) = (status, body, mediaType, null);
        }

        public void Throw(Exception exception) => _exception = exception;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body));
            if (_exception is not null)
            {
                throw _exception;
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, _mediaType) };
        }
    }
}
