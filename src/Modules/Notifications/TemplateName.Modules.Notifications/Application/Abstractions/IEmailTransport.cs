namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// Hands an email to a mail server (<c>SmtpEmailTransport</c>; the integration tests record instead). The email channel is its only
/// caller.
/// </summary>
internal interface IEmailTransport
{
    /// <summary>
    /// Sends <paramref name="message"/>. A failure the server or the network reports throws <see cref="EmailSendException"/>, which says
    /// whether it is permanent and carries a short reason code; cancelling <paramref name="cancellationToken"/> throws
    /// <see cref="OperationCanceledException"/>. Never logs the address or the bodies.
    /// </summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
