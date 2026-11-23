namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// Sends one push to one device through the push provider (FCM). It returns once the provider accepted the message and throws
/// <see cref="PushSendException"/> otherwise; it never logs or throws the token, the title or the body.
/// </summary>
internal interface IPushTransport
{
    Task SendAsync(PushMessage message, CancellationToken cancellationToken);
}
