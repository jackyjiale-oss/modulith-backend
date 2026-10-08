using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Infrastructure.Security;

namespace TemplateName.UnitTests.Auth;

public sealed class HibpBreachedPasswordCheckerTests
{
    private const string Password = "P@ssw0rd-for-tests";

    private static readonly string FullHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Password)));
    private static readonly string Prefix = FullHash[..5];
    private static readonly string Suffix = FullHash[5..];

    [Fact]
    public async Task Reports_breached_when_suffix_is_listed()
    {
        var handler = RespondingWith($"0018A45C4D1DEF81644B54AB7F969B88D65:3\r\n{Suffix}:42\r\n00D4F6E8FA6EECAD2A3AA415EEC418D38EC:2");

        var breached = await CheckerFor(handler).IsBreachedAsync(Password, TestContext.Current.CancellationToken);

        breached.ShouldBeTrue();
    }

    [Fact]
    public async Task Reports_clean_when_suffix_is_absent()
    {
        var handler = RespondingWith("0018A45C4D1DEF81644B54AB7F969B88D65:3\r\n00D4F6E8FA6EECAD2A3AA415EEC418D38EC:2");

        var breached = await CheckerFor(handler).IsBreachedAsync(Password, TestContext.Current.CancellationToken);

        breached.ShouldBeFalse();
    }

    [Fact]
    public async Task Ignores_padding_rows_with_a_zero_count()
    {
        var handler = RespondingWith($"{Suffix.ToLowerInvariant()}:0\r\n0018A45C4D1DEF81644B54AB7F969B88D65:3");

        var breached = await CheckerFor(handler).IsBreachedAsync(Password, TestContext.Current.CancellationToken);

        breached.ShouldBeFalse();
    }

    [Fact]
    public async Task Sends_only_the_five_character_prefix_and_requests_padding()
    {
        var handler = RespondingWith("0018A45C4D1DEF81644B54AB7F969B88D65:3");

        await CheckerFor(handler).IsBreachedAsync(Password, TestContext.Current.CancellationToken);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Get);
        request.Uri.Scheme.ShouldBe("https");
        request.Uri.Host.ShouldBe("api.pwnedpasswords.com");
        request.Uri.AbsolutePath.ShouldBe($"/range/{Prefix}");
        request.Uri.Query.ShouldBeEmpty();
        request.Headers["Add-Padding"].ShouldBe("true");

        var everything = request.Uri + string.Join(' ', request.Headers.Values);
        everything.ShouldNotContain(Password);
        everything.ShouldNotContain(FullHash, Case.Insensitive);
        everything.ShouldNotContain(Suffix, Case.Insensitive);
    }

    [Theory]
    [InlineData("http-error")]
    [InlineData("timeout")]
    [InlineData("exception")]
    public async Task Fails_open_on_http_error_timeout_and_exception_and_logs_a_warning(string failure)
    {
        var handler = new StubHandler(_ => failure switch
        {
            "http-error" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "timeout" => throw new TaskCanceledException("timed out", new TimeoutException()),
            _ => throw new HttpRequestException("connection refused"),
        });
        var logger = new CapturingLogger<HibpBreachedPasswordChecker>();

        var breached = await CheckerFor(handler, logger: logger).IsBreachedAsync(Password, TestContext.Current.CancellationToken);

        breached.ShouldBeFalse();
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldNotContain(Password);
        entry.Message.ShouldNotContain(Prefix, Case.Insensitive);
        entry.Message.ShouldNotContain(Suffix, Case.Insensitive);
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_swallowed_as_fail_open()
    {
        var handler = new StubHandler((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => CheckerFor(handler).IsBreachedAsync(Password, cancellation.Token));
    }

    [Fact]
    public async Task Skips_the_request_when_CheckBreached_is_false()
    {
        var handler = RespondingWith($"{Suffix}:42");

        var breached = await CheckerFor(handler, checkBreached: false).IsBreachedAsync(Password, TestContext.Current.CancellationToken);

        breached.ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
    }

    private static StubHandler RespondingWith(string body) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    private static HibpBreachedPasswordChecker CheckerFor(
        StubHandler handler,
        bool checkBreached = true,
        CapturingLogger<HibpBreachedPasswordChecker>? logger = null) =>
        new(
            new StubHttpClientFactory(new HttpClient(handler)),
            Options.Create(new PasswordOptions { CheckBreached = checkBreached }),
            logger ?? new CapturingLogger<HibpBreachedPasswordChecker>());

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => respond(request))
        {
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(',', header.Value))));
            return Task.FromResult(respond(request, cancellationToken));
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.ShouldBe(HibpBreachedPasswordChecker.HttpClientName);
            return client;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception) + exception));
    }
}
