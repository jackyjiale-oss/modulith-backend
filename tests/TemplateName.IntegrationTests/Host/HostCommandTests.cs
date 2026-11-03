using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TemplateName.Api;

namespace TemplateName.IntegrationTests.Host;

/// <summary>The <c>healthcheck</c> probe and the command-line recognition of the host; no SQL Server and no test host.</summary>
public sealed class HostCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("migrate", nameof(HostCommandKind.Migrate))]
    [InlineData("healthcheck", nameof(HostCommandKind.Healthcheck))]
    public void Parse_recognises_only_the_exact_first_argument(string argument, string expected)
        => HostCommand.Parse([argument]).ToString().ShouldBe(expected);

    [Theory]
    [MemberData(nameof(UnrecognisedArguments))]
    public void Parse_lets_every_other_argument_list_fall_through(string[] args)
        => HostCommand.Parse(args).ToString().ShouldBe(nameof(HostCommandKind.None));

    public static TheoryData<string[]> UnrecognisedArguments => new()
    {
        { [] },
        { ["--urls", "http://x"] },
        { ["Migrate"] },
        { ["--migrate"] },
        { ["x", "migrate"] },
    };

    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("  ", 8080)]
    [InlineData("abc", 8080)]
    [InlineData("http://+:9000", 8080)]
    [InlineData("5000", 5000)]
    [InlineData("8081;8082", 8081)]
    [InlineData("x;7000", 7000)]
    public void HealthUrl_uses_the_first_port_and_falls_back_to_8080(string? httpPorts, int expectedPort)
        => HostCommand.HealthUrl(httpPorts).ToString().ShouldBe($"http://127.0.0.1:{expectedPort}/health/live");

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Healthcheck_exits_0_on_2xx(HttpStatusCode status)
    {
        using var output = new StringWriter();

        var exitCode = await HostCommand.RunHealthCheckAsync("8080", output, new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status))), TimeSpan.FromSeconds(3), Ct);

        exitCode.ShouldBe(0);
        OneLine(output).ShouldContain(((int)status).ToString());
    }

    [Fact]
    public async Task Healthcheck_exits_1_on_5xx()
    {
        using var output = new StringWriter();

        var exitCode = await HostCommand.RunHealthCheckAsync(
            "8080",
            output,
            new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))),
            TimeSpan.FromSeconds(3),
            Ct);

        exitCode.ShouldBe(1);
        OneLine(output).ShouldContain("503");
    }

    [Fact]
    public async Task Healthcheck_exits_1_on_a_redirect()
    {
        using var output = new StringWriter();

        var exitCode = await HostCommand.RunHealthCheckAsync(
            "8080",
            output,
            new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect))),
            TimeSpan.FromSeconds(3),
            Ct);

        exitCode.ShouldBe(1);
        OneLine(output).ShouldBe("unhealthy (307)");
    }

    [Fact]
    public async Task Healthcheck_does_not_follow_a_redirect_with_the_real_handler()
    {
        // HTTPS redirection with an https port set answers /health/live with a 307; followed, it would reach a port nothing listens
        // on (or, here, a 200 from another path) and hide the misconfiguration. A loopback server answers both paths.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestedPaths = new List<string>();
        using var stopServer = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = ServeRedirectAsync(listener, port, requestedPaths, stopServer.Token);
        using var output = new StringWriter();

        var exitCode = await HostCommand.RunHealthCheckAsync(port.ToString(CultureInfo.InvariantCulture), output, null, TimeSpan.FromSeconds(3), Ct);

        await stopServer.CancelAsync();
        listener.Stop();
        // The server loop ends by throwing (cancelled, or the listener stopped); observed here so it is never unobserved.
        await Record.ExceptionAsync(() => server);
        exitCode.ShouldBe(1);
        OneLine(output).ShouldBe("unhealthy (307)");
        string.Join(",", requestedPaths).ShouldBe("/health/live");
    }

    [Fact]
    public async Task Healthcheck_exits_1_on_a_failed_request()
    {
        using var output = new StringWriter();

        var exitCode = await HostCommand.RunHealthCheckAsync(
            "8080",
            output,
            new StubHandler(_ => throw new HttpRequestException("connection refused")),
            TimeSpan.FromSeconds(3),
            Ct);

        exitCode.ShouldBe(1);
        OneLine(output).ShouldContain(nameof(HttpRequestException));
    }

    [Fact]
    public async Task Healthcheck_exits_1_when_the_timeout_passes()
    {
        using var output = new StringWriter();
        var handler = new StubHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var stopwatch = Stopwatch.StartNew();

        var exitCode = await HostCommand.RunHealthCheckAsync("8080", output, handler, TimeSpan.FromMilliseconds(100), Ct);

        exitCode.ShouldBe(1);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        OneLine(output);
    }

    [Fact]
    public async Task Healthcheck_exits_1_when_nothing_listens()
    {
        // A port that was free a moment ago: bind to 0, read the port, release it.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var output = new StringWriter();

        var exitCode = await HostCommand.RunHealthCheckAsync(port.ToString(), output, null, TimeSpan.FromSeconds(3), Ct);

        exitCode.ShouldBe(1);
        OneLine(output);
    }

    /// <summary>The single line the probe wrote: it ends with one newline and holds no other.</summary>
    private static string OneLine(StringWriter output)
    {
        var text = output.ToString();
        text.ShouldEndWith(Environment.NewLine);
        var line = text[..^Environment.NewLine.Length];
        line.ShouldNotBeEmpty();
        line.ShouldNotContain('\n');
        return line;
    }

    /// <summary>
    /// Answers each connection once, until cancelled: <c>/health/live</c> with a 307 to <c>/redirected</c> on the same port, any other path
    /// with a 200. Records every path asked for. Ends with an exception when cancelled or when the listener stops.
    /// </summary>
    private static async Task ServeRedirectAsync(TcpListener listener, int port, List<string> requestedPaths, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            var head = new StringBuilder();
            var buffer = new byte[4096];
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var path = head.ToString().Split(' ')[1];
            lock (requestedPaths)
            {
                requestedPaths.Add(path);
            }

            var response = path == "/health/live"
                ? $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }
    }

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(cancellationToken);
    }
}
