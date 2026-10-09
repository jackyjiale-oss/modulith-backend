using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// Renders a notification type's templates for one channel in the recipient's culture (ADR 0019). The variables are the only data a
/// template sees, plus <c>product_name</c>; secret variables are passed decrypted only by the caller that sends the message.
/// </summary>
internal interface INotificationRenderer
{
    /// <summary>
    /// Renders <paramref name="typeCode"/> for <paramref name="channel"/> in <paramref name="culture"/>, falling back to <c>en</c> for a
    /// template that does not exist in that culture.
    /// </summary>
    /// <exception cref="TemplateRenderException">
    /// The type is not declared, a template is missing in both cultures or does not parse, a template uses a variable that was not
    /// supplied, or it fails at run time. The exception never contains a variable value.
    /// </exception>
    RenderedMessage Render(string typeCode, NotificationChannel channel, string culture, IReadOnlyDictionary<string, string> variables);
}
