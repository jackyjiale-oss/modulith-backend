using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// A notification could not be rendered; the delivery worker treats it as a permanent failure. <see cref="Reason"/> names template
/// resources, parts and variable <em>names</em>, never a variable value: a value can be a secret (a single-use link) or personal data,
/// and the exception may be logged. For the same reason it never wraps the template engine's run-time exception, whose message can
/// quote a value.
/// </summary>
internal sealed class TemplateRenderException(string typeCode, NotificationChannel channel, string culture, string reason)
    : Exception($"Cannot render the {channel} message of notification type '{typeCode}' in culture '{culture}': {reason}.")
{
    /// <summary>The notification type.</summary>
    public string TypeCode { get; } = typeCode;

    /// <summary>The channel being rendered.</summary>
    public NotificationChannel Channel { get; } = channel;

    /// <summary>The culture asked for (before the <c>en</c> fallback).</summary>
    public string Culture { get; } = culture;

    /// <summary>What went wrong, without any variable value.</summary>
    public string Reason { get; } = reason;
}
