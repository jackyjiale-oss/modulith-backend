namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Sends an email. It throws when the message could not be handed over, so the outbox retries the event that asked for it.</summary>
internal interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>An email to one recipient. <paramref name="HtmlBody"/> is optional; the text body is always present.</summary>
/// <param name="To">The recipient's address.</param>
/// <param name="Subject">The subject; it never holds user-supplied text.</param>
/// <param name="TextBody">The plain-text body.</param>
/// <param name="HtmlBody">The HTML body, in which every user-supplied value is already encoded; <see langword="null"/> for a text-only message.</param>
internal sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody);
