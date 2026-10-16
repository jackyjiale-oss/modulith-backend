using Serilog.Core;
using Serilog.Events;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// A Serilog sink that keeps every event it receives in memory, after Serilog's enrichers ran. A test host registers it as an
/// <see cref="ILogEventSink"/> service, which the host's Serilog pipeline picks up (<c>ReadFrom.Services</c>), to assert what Serilog
/// would write: the request events of <c>UseRequestLogging</c> and every Microsoft.Extensions.Logging line that reaches Serilog.
/// Thread-safe.
/// </summary>
internal sealed class RecordingLogEventSink : ILogEventSink
{
    private readonly Lock _lock = new();
    private readonly List<LogEvent> _events = [];

    /// <summary>A snapshot of the events so far.</summary>
    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>One string per event: the source, the rendered message, every property and the exception's full text.</summary>
    public IReadOnlyList<string> Entries
        => [.. Events.Select(logEvent =>
            $"{logEvent.Level} {logEvent.RenderMessage()} | {string.Join(" ", logEvent.Properties.Select(property => $"{property.Key}={property.Value}"))} | {logEvent.Exception}")];

    public void Emit(LogEvent logEvent)
    {
        lock (_lock)
        {
            _events.Add(logEvent);
        }
    }
}
