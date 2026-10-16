using Microsoft.Extensions.Logging;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Keeps every log entry of every level in memory: the formatted message, each structured value and the exception's full text. A test
/// host uses it as its only logger provider (<c>new LoggerFactory([provider])</c> in place of Serilog's factory) to assert what is
/// never logged. Thread-safe.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly Lock _lock = new();
    private readonly List<string> _entries = [];

    /// <summary>A snapshot of the entries so far: one string per entry with the message, the structured values and the exception.</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(string entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }
    }

    private sealed class RecordingLogger(RecordingLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;

            provider.Add($"{logLevel} {categoryName}: {formatter(state, exception)} | {values} | {exception}");
        }
    }
}
