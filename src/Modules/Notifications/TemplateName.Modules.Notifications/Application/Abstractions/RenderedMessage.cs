namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// A notification rendered for one channel. Email fills all three: <see cref="Subject"/> from the <c>subject</c> part,
/// <see cref="TextBody"/> from <c>text</c> and <see cref="HtmlBody"/> from <c>html</c> inside the culture's layout. In-app puts the
/// <c>title</c> part in <see cref="Subject"/> and the <c>body</c> part in <see cref="TextBody"/>; <see cref="HtmlBody"/> is null.
/// <see cref="Subject"/> is a single line of at most 300 characters. Only <see cref="HtmlBody"/> is HTML; the others are plain text.
/// </summary>
internal sealed record RenderedMessage(string Subject, string TextBody, string? HtmlBody);
