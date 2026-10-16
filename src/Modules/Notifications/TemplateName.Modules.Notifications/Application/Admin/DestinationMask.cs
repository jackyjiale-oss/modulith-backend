using System.Globalization;

namespace TemplateName.Modules.Notifications.Application.Admin;

/// <summary>
/// Hides most of a delivery's destination in administrators' views: an address is personal data, and an administrator who needs to
/// tell deliveries apart needs the domain and a hint of the recipient, not the address.
/// </summary>
/// <remarks>
/// The local part is replaced by its first character and four asterisks (<c>alice@example.com</c> becomes <c>a****@example.com</c>);
/// the number of asterisks never depends on the real length, so the length is not leaked. The first character is shown only when the
/// local part has at least <see cref="MinLocalPartForHint"/> characters, because for a shorter one it would be half of the secret or all
/// of it. The domain is not secret and stays whole. Anything that does not look like an address is masked entirely.
/// </remarks>
internal static class DestinationMask
{
    /// <summary>The shortest local part (in text elements, so a letter with its accents or an emoji counts once) that shows its first one.</summary>
    internal const int MinLocalPartForHint = 3;

    private const string Hidden = "****";

    /// <summary>
    /// The masked form of <paramref name="destination"/>: <see langword="null"/> for none (in-app deliveries have no destination, and a
    /// blank value has nothing to hide); <c>first****@domain</c> for an address; <c>****@domain</c> when the local part is too short to
    /// show anything; <c>****</c> when there is no <c>@</c>, no domain or no local part to separate. The address is split at its last
    /// <c>@</c>, the only one a domain can follow.
    /// </summary>
    public static string? Mask(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        var at = destination.LastIndexOf('@');
        if (at < 0 || at == destination.Length - 1)
        {
            return Hidden;
        }

        var local = destination.AsSpan(0, at);
        var domain = destination.AsSpan(at + 1);
        if (local.IsEmpty)
        {
            return Hidden;
        }

        return CountTextElements(local) >= MinLocalPartForHint
            ? string.Concat(destination.AsSpan(0, StringInfo.GetNextTextElementLength(local)), Hidden, "@", domain)
            : string.Concat(Hidden, "@", domain);
    }

    private static int CountTextElements(ReadOnlySpan<char> text)
    {
        var count = 0;
        while (!text.IsEmpty)
        {
            text = text[StringInfo.GetNextTextElementLength(text)..];
            if (++count >= MinLocalPartForHint)
            {
                break;
            }
        }

        return count;
    }
}
