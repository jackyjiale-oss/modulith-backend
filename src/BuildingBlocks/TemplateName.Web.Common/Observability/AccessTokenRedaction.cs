using System.Text.RegularExpressions;

namespace TemplateName.Web.Common.Observability;

/// <summary>
/// Masks the value of every <c>access_token</c> and <c>input_token</c> query parameter in a text (a query string or a whole URL):
/// <c>?id=1&amp;access_token=abc</c> becomes <c>?id=1&amp;access_token=[Redacted]</c>. SignalR clients that cannot send headers (browsers)
/// put their credential in <c>access_token</c> (blueprint review 2.10, ADR 0021), and the Graph API calls of social login carry the
/// user's Facebook token as <c>input_token</c> and <c>access_token</c> (and the app's <c>{AppId}|{AppSecret}</c> as <c>access_token</c>),
/// so none of them may reach a log or a trace. The names are matched without regard to case, and also in their percent-encoded form
/// (<c>access%5Ftoken</c>, <c>input%5Ftoken</c>), which ASP.NET Core decodes to the same parameter.
/// </summary>
public static partial class AccessTokenRedaction
{
    /// <summary>What replaces each value.</summary>
    public const string Redacted = "[Redacted]";

    /// <summary>Whether <paramref name="text"/> holds an <c>access_token</c> or <c>input_token</c> parameter, the cheap test to make before <see cref="Redact"/>.</summary>
    public static bool ContainsAccessToken(string? text)
        => text is not null
            && (text.Contains("access_token=", StringComparison.OrdinalIgnoreCase)
                || text.Contains("access%5Ftoken=", StringComparison.OrdinalIgnoreCase)
                || text.Contains("input_token=", StringComparison.OrdinalIgnoreCase)
                || text.Contains("input%5Ftoken=", StringComparison.OrdinalIgnoreCase));

    /// <summary><paramref name="text"/> with every <c>access_token</c> and <c>input_token</c> value replaced by <see cref="Redacted"/>; unchanged without one.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return ContainsAccessToken(text) ? TokenValue().Replace(text, Redacted) : text;
    }

    // The value after access_token= or input_token= at the start of the text, or after ? or &, up to the next & or # (or white space,
    // in a log line).
    [GeneratedRegex(@"(?<=(?:^|[?&])(?:access|input)(?:_|%5F)token=)[^&#\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenValue();
}
