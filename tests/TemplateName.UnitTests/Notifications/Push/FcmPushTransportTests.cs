using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NSubstitute;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Push;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Notifications.Push;

public sealed class FcmPushTransportTests
{
    private const string DeviceToken = "device-token-secret-123";
    private const string AccessToken = "oauth-access-token";
    private const string ProjectId = "demo-project";

    private readonly StubHandler _handler = new();
    private readonly IFcmAccessTokenSource _tokens = Substitute.For<IFcmAccessTokenSource>();
    private readonly RecordingLogger<FcmPushTransport> _logger = new();

    public FcmPushTransportTests() => _tokens.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(AccessToken);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<HttpStatusCode, string?, string, bool, bool> FailureTable => new()
    {
        { HttpStatusCode.NotFound, "UNREGISTERED", "fcm_unregistered", true, true },
        { HttpStatusCode.Forbidden, "SENDER_ID_MISMATCH", "fcm_sender_id_mismatch", true, true },
        { HttpStatusCode.BadRequest, "INVALID_ARGUMENT", "fcm_invalid_argument", true, false },
        { HttpStatusCode.TooManyRequests, "QUOTA_EXCEEDED", "fcm_quota_exceeded", false, false },
        { HttpStatusCode.ServiceUnavailable, "UNAVAILABLE", "fcm_unavailable", false, false },
        { HttpStatusCode.InternalServerError, "INTERNAL", "fcm_internal", false, false },
        { HttpStatusCode.Unauthorized, "THIRD_PARTY_AUTH_ERROR", "fcm_third_party_auth_error", false, false },
        // Without an FCM error code the status decides, and a status alone never deactivates a device: a 403 or 404 without the code
        // may be the project's configuration, not the token.
        { HttpStatusCode.NotFound, null, "fcm_http_404", true, false },
        { HttpStatusCode.Forbidden, null, "fcm_http_403", false, false },
        { HttpStatusCode.Unauthorized, null, "fcm_http_401", false, false },
        { HttpStatusCode.BadRequest, null, "fcm_http_400", true, false },
        { HttpStatusCode.TooManyRequests, null, "fcm_http_429", false, false },
        { HttpStatusCode.BadGateway, null, "fcm_http_502", false, false },
    };

    [Fact]
    public async Task Sends_the_v1_request_with_the_bearer_token()
    {
        await Transport().SendAsync(Message(urgent: true), Ct);

        var request = _handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri($"https://fcm.googleapis.com/v1/projects/{ProjectId}/messages:send"));
        request.Authorization.ShouldBe($"Bearer {AccessToken}");
        var message = JsonDocument.Parse(request.Body).RootElement.GetProperty("message");
        message.GetProperty("token").GetString().ShouldBe(DeviceToken);
        message.GetProperty("notification").GetProperty("title").GetString().ShouldBe("Title");
        message.GetProperty("notification").GetProperty("body").GetString().ShouldBe("Body");
        message.GetProperty("data").GetProperty("typeCode").GetString().ShouldBe("auth.new_device_login");
        message.GetProperty("android").GetProperty("priority").GetString().ShouldBe("HIGH");
        message.GetProperty("apns").GetProperty("headers").GetProperty("apns-priority").GetString().ShouldBe("10");
    }

    [Fact]
    public async Task Not_urgent_uses_normal_and_5()
    {
        await Transport().SendAsync(Message(urgent: false), Ct);

        var message = JsonDocument.Parse(_handler.Requests.ShouldHaveSingleItem().Body).RootElement.GetProperty("message");
        message.GetProperty("android").GetProperty("priority").GetString().ShouldBe("NORMAL");
        message.GetProperty("apns").GetProperty("headers").GetProperty("apns-priority").GetString().ShouldBe("5");
    }

    [Theory]
    [MemberData(nameof(FailureTable))]
    public async Task Failures_map_to_reason_codes(HttpStatusCode status, string? errorCode, string reason, bool permanent, bool deactivate)
    {
        _handler.Respond(status, errorCode is null ? """{"error":{"code":0,"status":"X"}}""" : ErrorBody(errorCode));

        var failure = await Should.ThrowAsync<PushSendException>(() => Transport().SendAsync(Message(), Ct));

        failure.Reason.ShouldBe(reason);
        failure.IsPermanent.ShouldBe(permanent);
        failure.DeactivateDevice.ShouldBe(deactivate);
    }

    [Fact]
    public async Task A_non_json_error_body_falls_back_to_the_status()
    {
        _handler.Respond(HttpStatusCode.BadGateway, "<html><body>Bad gateway</body></html>", "text/html");

        var failure = await Should.ThrowAsync<PushSendException>(() => Transport().SendAsync(Message(), Ct));

        failure.Reason.ShouldBe("fcm_http_502");
        failure.IsPermanent.ShouldBeFalse();
    }

    [Fact]
    public async Task A_connection_failure_is_transient()
    {
        _handler.Throw(new HttpRequestException("connection refused"));

        var failure = await Should.ThrowAsync<PushSendException>(() => Transport().SendAsync(Message(), Ct));

        failure.Reason.ShouldBe("connection_failed");
        failure.IsPermanent.ShouldBeFalse();
    }

    [Fact]
    public async Task A_timeout_is_transient()
    {
        _handler.Throw(new TaskCanceledException("timed out"));

        var failure = await Should.ThrowAsync<PushSendException>(() => Transport().SendAsync(Message(), Ct));

        failure.Reason.ShouldBe("timeout");
    }

    [Fact]
    public async Task The_token_never_appears_in_logs_or_exceptions()
    {
        _handler.Respond(HttpStatusCode.NotFound, ErrorBody("UNREGISTERED", $"token {DeviceToken} is not registered"));

        var failure = await Should.ThrowAsync<PushSendException>(() => Transport().SendAsync(Message(), Ct));
        _handler.Respond(HttpStatusCode.OK, """{"name":"projects/demo-project/messages/1"}""");
        await Transport().SendAsync(Message(), Ct);

        failure.Message.ShouldNotContain(DeviceToken);
        failure.InnerException.ShouldBeNull();
        _logger.Entries.ShouldNotBeEmpty();
        _logger.Entries.ShouldAllBe(entry => !entry.Message.Contains(DeviceToken) && !entry.Message.Contains("Title") && !entry.Message.Contains("Body"));
        _logger.Exceptions.ShouldBeEmpty();
    }

    private static string ErrorBody(string errorCode, string message = "error") =>
        $$$"""{"error":{"code":400,"message":"{{{message}}}","status":"X","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"{{{errorCode}}}"}]}}""";

    private static PushMessage Message(bool urgent = true) =>
        new(Guid.NewGuid(), Guid.NewGuid(), DeviceToken, "Title", "Body", new Dictionary<string, string> { ["typeCode"] = "auth.new_device_login" }, urgent);

    private FcmPushTransport Transport()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(FcmPushTransport.ClientName).Returns(_ => new HttpClient(_handler, disposeHandler: false));
        return new FcmPushTransport(factory, _tokens, Options.Create(new FcmOptions { ProjectId = ProjectId }), _logger);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = """{"name":"projects/demo-project/messages/1"}""";
        private string _mediaType = "application/json";
        private Exception? _exception;

        public List<(HttpMethod Method, Uri? Uri, string? Authorization, string Body)> Requests { get; } = [];

        public void Respond(HttpStatusCode status, string body, string mediaType = "application/json")
        {
            (_status, _body, _mediaType, _exception) = (status, body, mediaType, null);
        }

        public void Throw(Exception exception) => _exception = exception;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), body));
            if (_exception is not null)
            {
                throw _exception;
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, _mediaType) };
        }
    }
}
