using Serilog.Core;
using Serilog.Events;

namespace TemplateName.UnitTests.Web;

internal sealed class RecordingLogEventSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = [];

    public void Emit(LogEvent logEvent) => Events.Add(logEvent);
}
