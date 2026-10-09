using System.Globalization;

namespace TemplateName.Modules.Notifications.Application.Catalog;

/// <summary>
/// Maps a recipient's saved locale to the UI culture a notification is rendered in. It looks only at the locale it is given: never at
/// <see cref="CultureInfo.CurrentUICulture"/>, because the culture of the request that triggered a notification (or of the host) is
/// not the recipient's.
/// </summary>
internal static class RecipientCulture
{
    /// <summary>The culture used when the locale is missing, invalid or not supported.</summary>
    public const string Default = "en";

    /// <summary>The cultures templates exist in.</summary>
    public static IReadOnlyList<string> Supported { get; } = ["en", "ms", "zh-Hans"];

    /// <summary>
    /// The first supported culture among <paramref name="locale"/> and its parents (<c>zh-CN</c> to <c>zh-Hans</c>, <c>ms-MY</c> to
    /// <c>ms</c>), else <see cref="Default"/> for a null, blank, invalid or unsupported locale.
    /// </summary>
    public static string Resolve(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return Default;
        }

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(locale.Trim(), predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return Default;
        }

        for (; culture.Name.Length > 0; culture = culture.Parent)
        {
            foreach (var supported in Supported)
            {
                if (string.Equals(culture.Name, supported, StringComparison.OrdinalIgnoreCase))
                {
                    return supported;
                }
            }
        }

        return Default;
    }
}
