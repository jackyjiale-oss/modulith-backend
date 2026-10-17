using Serilog.Core;
using Serilog.Events;

namespace TemplateName.Web.Common.Observability;

/// <summary>
/// Replaces the <c>access_token</c> values (<see cref="AccessTokenRedaction"/>) in every string property of a log event before any
/// sink sees it. Serilog renders a message from its properties, so this also covers the message text: ASP.NET Core's own
/// <c>Request starting</c> and <c>Request finished</c> lines, for example, carry the query string as the <c>QueryString</c> property.
/// Those lines are off by default (<c>Microsoft.AspNetCore</c> at <c>Warning</c>); this makes turning them on safe.
/// </summary>
public sealed class AccessTokenRedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        List<LogEventProperty>? redacted = null;
        foreach (var (name, value) in logEvent.Properties)
        {
            if (value is ScalarValue { Value: string text } && AccessTokenRedaction.ContainsAccessToken(text))
            {
                (redacted ??= []).Add(new LogEventProperty(name, new ScalarValue(AccessTokenRedaction.Redact(text))));
            }
        }

        foreach (var property in redacted ?? [])
        {
            logEvent.AddOrUpdateProperty(property);
        }
    }
}
