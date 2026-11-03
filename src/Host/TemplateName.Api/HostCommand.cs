using System.Globalization;
using Microsoft.Extensions.Logging;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.Api;

/// <summary>The command the host was started with: the web host itself, or one of the one-shot modes (ADR 0006).</summary>
internal enum HostCommandKind
{
    /// <summary>No command: run the web host. Every argument other than the two names below, <c>--urls</c> included, lands here.</summary>
    None,

    /// <summary>Apply the pending migrations of every module and exit; no Kestrel and no background service runs.</summary>
    Migrate,

    /// <summary>Ask the running host on this machine whether it is alive and exit; no host is built (the container <c>HEALTHCHECK</c>).</summary>
    Healthcheck,
}

/// <summary>The <c>migrate</c> and <c>healthcheck</c> modes of the API: recognising them and what each one does. Exit code 0 is success, 1 failure.</summary>
internal static partial class HostCommand
{
    private const int DefaultPort = 8080;

    /// <summary>Recognises a command from the first argument only, exact and case-sensitive; anything else is <see cref="HostCommandKind.None"/>.</summary>
    public static HostCommandKind Parse(string[] args)
        => args is [var first, ..]
            ? first switch
            {
                "migrate" => HostCommandKind.Migrate,
                "healthcheck" => HostCommandKind.Healthcheck,
                _ => HostCommandKind.None,
            }
            : HostCommandKind.None;

    /// <summary>
    /// The liveness URL on this machine: the first entry of <paramref name="httpPorts"/> (<c>ASPNETCORE_HTTP_PORTS</c>, a semicolon-separated
    /// list) that is a port number, or 8080 (the container's port) when there is none.
    /// </summary>
    public static Uri HealthUrl(string? httpPorts)
    {
        var port = (httpPorts ?? string.Empty)
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => int.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 65535 ? parsed : (int?)null)
            .FirstOrDefault(parsed => parsed is not null) ?? DefaultPort;

        return new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/health/live");
    }

    /// <summary>
    /// Makes one GET to the liveness endpoint and writes one line to <paramref name="output"/>: the status code, or the kind of failure.
    /// Returns 0 for a 2xx answer and 1 for anything else, a timeout or a refused connection included. A redirect is not followed, so
    /// HTTPS redirection switched on by an https port reads <c>unhealthy (307)</c> instead of a failure somewhere else.
    /// </summary>
    /// <param name="httpPorts">The value of <c>ASPNETCORE_HTTP_PORTS</c>.</param>
    /// <param name="output">Where the line goes.</param>
    /// <param name="handler">The HTTP handler; <see langword="null"/> uses the real one (tests pass a stub).</param>
    /// <param name="timeout">How long the whole request may take.</param>
    public static async Task<int> RunHealthCheckAsync(
        string? httpPorts,
        TextWriter output,
        HttpMessageHandler? handler,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true) { Timeout = timeout };

        try
        {
            using var response = await client.GetAsync(HealthUrl(httpPorts), cancellationToken);
            var status = (int)response.StatusCode;
            await output.WriteLineAsync(response.IsSuccessStatusCode ? $"healthy ({status})" : $"unhealthy ({status})");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception)
        {
            await output.WriteLineAsync($"unhealthy ({exception.GetType().Name})");
            return 1;
        }
    }

    /// <summary>
    /// Applies the pending migrations of every module (<see cref="MigrationExtensions.MigrateModuleDatabasesAsync"/>). Returns 0 on
    /// success; on any failure, a cancellation included, logs the exception at Error and returns 1. The connection string is never logged.
    /// </summary>
    public static async Task<int> RunMigrationsAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await services.MigrateModuleDatabasesAsync(cancellationToken);
            LogMigrated(logger);
            return 0;
        }
        catch (Exception exception)
        {
            LogMigrationFailed(logger, exception);
            return 1;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The module databases are migrated")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Migrating the module databases failed")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);
}
