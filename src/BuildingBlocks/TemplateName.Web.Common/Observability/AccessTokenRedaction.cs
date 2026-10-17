using System.Text.RegularExpressions;

namespace TemplateName.Web.Common.Observability;

/// <summary>
/// Masks the value of every <c>access_token</c> query parameter in a text (a query string or a whole URL): <c>?id=1&amp;access_token=abc</c>
/// becomes <c>?id=1&amp;access_token=[Redacted]</c>. SignalR clients that cannot send headers (browsers) put their credential there, so
/// it must not reach a log or a trace (blueprint review 2.10, ADR 0021). The name is matched without regard to case, and also in its
/// percent-encoded form (<c>access%5Ftoken</c>), which ASP.NET Core decodes to the same parameter.
/// </summary>
public static partial class AccessTokenRedaction
{
    /// <summary>What replaces each value.</summary>
    public const string Redacted = "[Redacted]";

    /// <summary>Whether <paramref name="text"/> holds an <c>access_token</c> parameter, the cheap test to make before <see cref="Redact"/>.</summary>
    public static bool ContainsAccessToken(string? text)
        => text is not null
            && (text.Contains("access_token=", StringComparison.OrdinalIgnoreCase) || text.Contains("access%5Ftoken=", StringComparison.OrdinalIgnoreCase));

    /// <summary><paramref name="text"/> with every <c>access_token</c> value replaced by <see cref="Redacted"/>; unchanged without one.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return ContainsAccessToken(text) ? AccessTokenValue().Replace(text, Redacted) : text;
    }

    // The value after access_token= at the start of the text, or after ? or &, up to the next & or # (or white space, in a log line).
    [GeneratedRegex(@"(?<=(?:^|[?&])access(?:_|%5F)token=)[^&#\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenValue();
}
