using Microsoft.AspNetCore.Http;
using TemplateName.Application.Common.Concurrency;
using TemplateName.SharedKernel;
using TemplateName.Web.Common.Results;

namespace TemplateName.Web.Common.Concurrency;

/// <summary>
/// Reads an <c>If-Match</c> header (RFC 9110, 13.1.1) into the <see cref="IfMatch"/> a command carries (ADR 0038). Absent is
/// <see cref="IfMatch.None"/>; <c>*</c> alone is <see cref="IfMatch.Any"/>; otherwise a comma-separated list of quoted tags, where a weak
/// tag (<c>W/"..."</c>) is accepted but never matches. Anything else (an empty value, a missing quote, a character outside a tag's
/// characters, an empty list element, <c>*</c> beside a tag) is malformed: a client bug answered with 400 <c>request.malformed</c>.
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

        var trimmed = header.Trim();
        if (trimmed == "*")
        {
            ifMatch = IfMatch.Any;
            return true;
        }

        if (trimmed.Length == 0)
        {
            return false;
        }

        var strongTags = new List<EntityTag>();
        foreach (var element in trimmed.Split(','))
        {
            var item = element.Trim();
            var weak = item.StartsWith(WeakPrefix, StringComparison.Ordinal);
            var quoted = weak ? item[WeakPrefix.Length..] : item;
            if (!IsQuotedTag(quoted))
            {
                return false;
            }

            if (!weak)
            {
                strongTags.Add(new EntityTag(quoted[1..^1]));
            }
        }

        ifMatch = IfMatch.Of(strongTags);
        return true;
    }

    /// <summary>The 400 problem for a malformed header (<c>request.malformed</c>).</summary>
    public static IResult Malformed() => Result.Failure(ConcurrencyErrors.MalformedIfMatch).ToProblem();

    private static bool IsQuotedTag(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return false;
        }

        foreach (var character in value.AsSpan(1, value.Length - 2))
        {
            if (!IsTagCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    // RFC 9110 etagc: %x21 / %x23-7E / obs-text (%x80-FF); so no space, no double quote, no control character.
    private static bool IsTagCharacter(char character)
        => character == '\x21' || character is >= '\x23' and <= '\x7E' || character is >= '\x80' and <= '\xFF';
}
