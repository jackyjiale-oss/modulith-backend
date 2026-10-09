using System.Globalization;

namespace TemplateName.Modules.Auth.Application.Admin.AuditLogs.List;

/// <summary>
/// Reads the <c>from</c> and <c>to</c> filters of the audit log: ISO 8601 dates (<c>2026-10-09</c>, midnight) or date-times with minutes,
/// seconds or fractional seconds (<c>2026-10-09T08:30:00.5Z</c>). A value with an offset (<c>Z</c>, <c>+08:00</c>) is converted to UTC;
/// one without is read as UTC, never in the server's time zone, so the same request means the same instants everywhere. Other spellings
/// (and so culture-dependent ones) are refused.
/// </summary>
internal static class AuditLogTimestamp
{
    // "K" is the offset (Z or +08:00); without it the value is read as UTC (DateTimeStyles.AssumeUniversal below).
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mmK",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
    ];

    /// <summary>Reads <paramref name="text"/> as an instant; <paramref name="utc"/> is its UTC <see cref="DateTime"/>.</summary>
    public static bool TryParse(string? text, out DateTime utc)
    {
        if (DateTimeOffset.TryParseExact(
                text,
                Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }

        utc = default;
        return false;
    }
}
