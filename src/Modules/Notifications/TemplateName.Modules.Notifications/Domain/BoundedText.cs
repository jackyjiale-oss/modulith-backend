using System.Diagnostics.CodeAnalysis;

namespace TemplateName.Modules.Notifications.Domain;

/// <summary>
/// The one place the module's domain cuts text to a column limit. Text that comes from outside (a rendered subject, a template title,
/// a trace id, a failure reason) is cut rather than failing the save of a notification that has already been decided.
/// </summary>
internal static class BoundedText
{
    /// <summary>
    /// Returns <paramref name="value"/> unchanged when it fits in <paramref name="maxLength"/> UTF-16 units, else its first
    /// <paramref name="maxLength"/> units, or one fewer when that would end in half of a surrogate pair.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Cut(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        var length = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;

        return value[..length];
    }
}
