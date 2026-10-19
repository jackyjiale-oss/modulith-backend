using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using TemplateName.Modules.Notifications.Domain.HubTickets;

namespace TemplateName.Modules.Notifications.Application.HubTickets;

/// <summary>
/// The format of a hub ticket: 32 bytes from <see cref="RandomNumberGenerator"/>, written as base64url without padding (43 characters,
/// safe in a query string). Only the SHA-256 hash of those bytes is stored (<see cref="HubTicket.TokenHash"/>). The ticket is a bearer
/// secret for its short life: it is never logged, stored, put in an exception message or in a metric tag.
/// </summary>
internal static class HubTicketTokens
{
    /// <summary>The length of a ticket: 32 bytes in base64url without padding.</summary>
    public const int TicketLength = 43;

    private const int TicketBytes = 32;

    /// <summary>A new ticket and the hash to store for it.</summary>
    public static (string Ticket, byte[] Hash) Create()
    {
        Span<byte> bytes = stackalloc byte[TicketBytes];
        RandomNumberGenerator.Fill(bytes);

        return (Base64Url.EncodeToString(bytes), SHA256.HashData(bytes));
    }

    /// <summary>
    /// The stored hash of <paramref name="ticket"/>, or false when the value is not shaped like a ticket (wrong length or not base64url),
    /// so it cannot be one and needs no lookup.
    /// </summary>
    public static bool TryHash(string? ticket, [NotNullWhen(true)] out byte[]? hash)
    {
        hash = null;
        if (ticket is not { Length: TicketLength })
        {
            return false;
        }

        // TryDecodeFromChars throws on a character outside the alphabet, so the value is checked first: a malformed one is refused,
        // never an exception (that would be a 500 on the hub path).
        Span<byte> bytes = stackalloc byte[TicketBytes];
        if (!Base64Url.IsValid(ticket, out var decodedLength)
            || decodedLength != TicketBytes
            || !Base64Url.TryDecodeFromChars(ticket, bytes, out var written)
            || written != TicketBytes)
        {
            return false;
        }

        hash = SHA256.HashData(bytes);
        return true;
    }
}
