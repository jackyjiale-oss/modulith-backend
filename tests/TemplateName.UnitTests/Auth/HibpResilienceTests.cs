using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Http;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Infrastructure.Security;
using TemplateName.UnitTests.Infrastructure.Http;

namespace TemplateName.UnitTests.Auth;

/// <summary>The real checker over the <c>hibp</c> pipeline of <c>AddExternalHttpClient</c> and a scripted handler, on fake time.</summary>
public sealed class HibpResilienceTests
{
    private const string Password = "P@ssw0rd-for-tests";

    private static readonly string FullHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Password)));
    private static readonly string Prefix = FullHash[..5];
    private static readonly string Suffix = FullHash[5..];

    [Fact]
    public async Task A_hung_server_returns_not_breached_within_the_two_second_budget()
    {
        var handler = ScriptedHttpMessageHandler.Hanging();
        var logger = new CapturingLogger();
        using var host = HostFor(handler);

        var started = host.Time.GetTimestamp();
        var breached = await host.RunAsync(CheckerFor(host, logger).IsBreachedAsync(Password, TestContext.Current.CancellationToken), TimeSpan.FromMilliseconds(100));
        var elapsed = host.Time.GetElapsedTime(started);

        breached.ShouldBeFalse();
        elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        elapsed.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(2.1));
        handler.Calls.ShouldBeInRange(1, 2);
        AssertOneWarningWithoutRequestDetails(logger);
    }

    [Fact]
    public async Task A_503_returns_not_breached_after_one_retry()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        using var host = HostFor(handler);

        var breached = await host.RunAsync(CheckerFor(host).IsBreachedAsync(Password, TestContext.Current.CancellationToken), TimeSpan.FromMilliseconds(100));

        breached.ShouldBeFalse();
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task An_open_breaker_returns_not_breached_without_a_request()
    {
        var handler = ScriptedHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        var logger = new CapturingLogger();
        using var host = HostFor(handler);
        var checker = CheckerFor(host, logger);

        // Every check is two failed attempts (the first and its one retry), so five checks reach the breaker's ten.
        for (var check = 0; check < 5; check++)
        {
            (await host.RunAsync(checker.IsBreachedAsync(Password, TestContext.Current.CancellationToken), TimeSpan.FromMilliseconds(100))).ShouldBeFalse();
        }

        handler.Calls.ShouldBe(10);
        logger.Entries.Clear();

        var breached = await checker.IsBreachedAsync(Password, TestContext.Current.CancellationToken);

        breached.ShouldBeFalse();
        handler.Calls.ShouldBe(10);
        AssertOneWarningWithoutRequestDetails(logger);
    }

    [Fact]
    public async Task A_body_over_the_cap_is_still_refused()
    {
        const int Cap = 1024;
        var handler = new ScriptedHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{Suffix}:42\r\n" + new string('A', Cap * 4)) }));
        var logger = new CapturingLogger();
        using var host = HostFor(handler);

        var breached = await CheckerFor(host, logger, Cap).IsBreachedAsync(Password, TestContext.Current.CancellationToken);

        breached.ShouldBeFalse();
        handler.Calls.ShouldBe(1);
        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task A_listed_password_is_still_reported_through_the_pipeline()
    {
        var handler = new ScriptedHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{Suffix}:42") }));
        using var host = HostFor(handler);

        var breached = await CheckerFor(host).IsBreachedAsync(Password, TestContext.Current.CancellationToken);

        breached.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_caller_is_still_not_swallowed_as_fail_open()
    {
        var handler = ScriptedHttpMessageHandler.Hanging();
        using var host = HostFor(handler);
        using var cancellation = new CancellationTokenSource();

        var check = CheckerFor(host).IsBreachedAsync(Password, cancellation.Token);
        await ResilienceTestHost.WaitForCallsAsync(handler, 1);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => check);
    }

    private static ResilienceTestHost HostFor(ScriptedHttpMessageHandler handler) =>
        ResilienceTestHost.Create(ResiliencePipelines.Hibp, handler, clientName: HibpBreachedPasswordChecker.HttpClientName);

    private static HibpBreachedPasswordChecker CheckerFor(ResilienceTestHost host, CapturingLogger? logger = null, int? maxBodyBytes = null) =>
        new(
            host.ClientFactory,
            Options.Create(new PasswordOptions { CheckBreached = true }),
            logger ?? new CapturingLogger(),
            HibpBreachedPasswordChecker.RequestBudget,
            maxBodyBytes ?? HibpBreachedPasswordChecker.MaxBodyBytes);

    private static void AssertOneWarningWithoutRequestDetails(CapturingLogger logger)
    {
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldNotContain(Password);
        entry.Message.ShouldNotContain(Prefix, Case.Insensitive);
        entry.Message.ShouldNotContain(Suffix, Case.Insensitive);
        entry.Message.ShouldNotContain("pwnedpasswords", Case.Insensitive);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<HibpBreachedPasswordChecker>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception) + exception));
    }
}
