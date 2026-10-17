using System.Text;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// A notification rendered for one channel. Email fills all three: <see cref="Subject"/> from the <c>subject</c> part,
/// <see cref="TextBody"/> from <c>text</c> and <see cref="HtmlBody"/> from <c>html</c> inside the culture's layout. In-app puts the
/// <c>title</c> part in <see cref="Subject"/> and the <c>body</c> part in <see cref="TextBody"/>; <see cref="HtmlBody"/> is null.
/// <see cref="Subject"/> is a single line of at most 300 characters. Only <see cref="HtmlBody"/> is HTML; the others are plain text.
/// An email's texts may hold a decrypted link, so <c>ToString</c> prints none of them.
/// </summary>
internal sealed record RenderedMessage(string Subject, string TextBody, string? HtmlBody)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("HasHtmlBody = ").Append(HtmlBody is not null);
        return true;
    }
}
