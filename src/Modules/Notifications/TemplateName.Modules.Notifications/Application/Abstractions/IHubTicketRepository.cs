using TemplateName.Modules.Notifications.Domain.HubTickets;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

internal interface IHubTicketRepository
{
    void Add(HubTicket ticket);

    /// <summary>
    /// Consumes the ticket with the SHA-256 hash <paramref name="tokenHash"/> and returns the user it was issued to, or
    /// <see langword="null"/> when it is unknown, already consumed or expired (<paramref name="now"/> is not before its expiry). One
    /// conditional statement decides it, so of two concurrent callers exactly one gets the user.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="tokenHash"/> is not <see cref="HubTicket.TokenHashLength"/> bytes.</exception>
    Task<Guid?> TryConsumeAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken);
}
