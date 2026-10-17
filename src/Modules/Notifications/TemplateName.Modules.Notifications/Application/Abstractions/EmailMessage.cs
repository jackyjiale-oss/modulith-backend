using System.Text;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// One email to one recipient, built by the email channel from a rendered notification. It may hold a decrypted single-use link, so it
/// lives only in memory for the attempt: never stored or logged, and <c>ToString</c> prints none of its values.
/// </summary>
/// <param name="To">The recipient's address (one plain address, checked when the delivery was created).</param>
/// <param name="Subject">The subject: one line, rendered from the type's <c>subject</c> part; it never holds a secret.</param>
/// <param name="TextBody">The plain-text body.</param>
/// <param name="HtmlBody">The HTML body, in which every value is already encoded; <see langword="null"/> for a text-only message.</param>
internal sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody)
{
    // Every member is an address or a rendered text that may hold a link: print whether there is an HTML part, nothing else.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("HasHtmlBody = ").Append(HtmlBody is not null);
        return true;
    }
}
