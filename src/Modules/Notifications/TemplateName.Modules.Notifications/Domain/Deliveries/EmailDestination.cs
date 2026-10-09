using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;

namespace TemplateName.Modules.Notifications.Domain.Deliveries;

/// <summary>
/// The rule for the destination of an email delivery: exactly one plain address (<c>alice@example.com</c>), at most
/// <see cref="Delivery.MaxDestinationLength"/> characters. A list (<c>a@x, b@y</c>), a display name (<c>Alice &lt;a@x&gt;</c>), white
/// space or a control character (a line break could inject a mail header) is refused, so nothing but one recipient can ever reach the
/// email channel.
/// </summary>
internal static class EmailDestination
{
    /// <summary>Whether <paramref name="address"/> is one plain email address that fits the column.</summary>
    public static bool IsValid([NotNullWhen(true)] string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > Delivery.MaxDestinationLength)
        {
            return false;
        }

        foreach (var character in address)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character) || character is ',' or ';')
            {
                return false;
            }
        }

        // The parser also accepts "<a@x>" and quoted display names; only an address that is exactly its own parse result passes.
        return MailAddress.TryCreate(address, out var parsed)
            && parsed.DisplayName.Length == 0
            && string.Equals(parsed.Address, address, StringComparison.Ordinal);
    }
}
