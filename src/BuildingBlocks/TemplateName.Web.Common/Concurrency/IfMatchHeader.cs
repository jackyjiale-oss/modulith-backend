using Microsoft.AspNetCore.Http;
using TemplateName.Application.Common.Concurrency;
using TemplateName.SharedKernel;
using TemplateName.Web.Common.Results;

namespace TemplateName.Web.Common.Concurrency;

/// <summary>
/// Reads an <c>If-Match</c> header (RFC 9110, 13.1.1) into the <see cref="IfMatch"/> a command carries (ADR 0038). Absent is
/// <see cref="IfMatch.None"/>; <c>*</c> alone is <see cref="IfMatch.Any"/>; otherwise a comma-separated list of quoted tags, where a weak
/// tag (<c>W/"..."</c>) is accepted but never matches. A tag is read whole, so a comma inside its quotes belongs to it; empty list elements
/// are ignored (RFC 9110, 5.6.1.2). Anything else (no tag at all, a missing quote, a character outside a tag's characters, two tags without
/// a comma, <c>*</c> beside a tag) is malformed: a client bug answered with 400 <c>request.malformed</c>.
/// </summary>
public static class IfMatchHeader
{
    private const string WeakPrefix = "W/";

    /// <summary>Parses the header value; false when it is malformed.</summary>
    public static bool TryParse(string? header, out IfMatch ifMatch)
    {
        ifMatch = IfMatch.None;
        if (header is null)
        {
            return true;
        }

        if (header.Trim() == "*")
        {
            ifMatch = IfMatch.Any;
            return true;
        }

        var strongTags = new List<EntityTag>();
        var tagCount = 0;
        var position = Skip(header, 0, includingCommas: true);
        while (position < header.Length)
        {
            var weak = string.CompareOrdinal(header, position, WeakPrefix, 0, WeakPrefix.Length) == 0;
            if (weak)
            {
                position += WeakPrefix.Length;
            }

            if (!TryReadQuotedTag(header, ref position, out var tag))
            {
                return false;
            }

            tagCount++;
            if (!weak)
            {
                strongTags.Add(tag);
            }

            // After a tag comes only whitespace, then a comma or the end.
            position = Skip(header, position, includingCommas: false);
            if (position < header.Length && header[position] != ',')
            {
                return false;
            }

            position = Skip(header, position, includingCommas: true);
        }

        if (tagCount == 0)
        {
            return false;
        }

        ifMatch = IfMatch.Of(strongTags);
        return true;
    }

    /// <summary>The 400 problem for a malformed header (<c>request.malformed</c>).</summary>
    public static IResult Malformed() => Result.Failure(ConcurrencyErrors.MalformedIfMatch).ToProblem();

    // Skips optional whitespace (RFC 9110 OWS: space and tab) and, between list elements, commas: an empty element is ignored.
    private static int Skip(string header, int position, bool includingCommas)
    {
        while (position < header.Length && (header[position] is ' ' or '\t' || (includingCommas && header[position] == ',')))
        {
            position++;
        }

        return position;
    }

    // Reads "..." at position into the text between the quotes and moves past the closing quote.
    private static bool TryReadQuotedTag(string header, ref int position, out EntityTag tag)
    {
        tag = default;
        if (position >= header.Length || header[position] != '"')
        {
            return false;
        }

        var end = position + 1;
        while (end < header.Length && header[end] != '"')
        {
            if (!IsTagCharacter(header[end]))
            {
                return false;
            }

            end++;
        }

        if (end == header.Length)
        {
            return false;
        }

        tag = new EntityTag(header[(position + 1)..end]);
        position = end + 1;
        return true;
    }

    // RFC 9110 etagc: %x21 / %x23-7E / obs-text (%x80-FF); so no space, no double quote, no control character.
    private static bool IsTagCharacter(char character)
        => character == '\x21' || character is >= '\x23' and <= '\x7E' || character is >= '\x80' and <= '\xFF';
}
