using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.HubTickets;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class HubTicketRepository(NotificationsDbContext context) : IHubTicketRepository
{
    // HubTicket.CanConsume in one statement: the database decides which of two concurrent callers wins (the loser matches no row).
    // Raw SQL skips the change tracker; ExpiresAt is exclusive (a ticket is spent at its expiry instant).
    private const string TryConsumeSql = """
        UPDATE notify.HubTickets
        SET ConsumedAt = @Now
        OUTPUT inserted.UserId
        WHERE TokenHash = @TokenHash
          AND ConsumedAt IS NULL
          AND ExpiresAt > @Now
        """;

    public void Add(HubTicket ticket) => context.Set<HubTicket>().Add(ticket);

    public async Task<Guid?> TryConsumeAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        var rows = await context.Database
            .SqlQueryRaw<ConsumedTicketRow>(
                TryConsumeSql,
                new SqlParameter("@Now", SqlDbType.DateTime2) { Value = now.UtcDateTime, Scale = 7 },
                new SqlParameter("@TokenHash", SqlDbType.VarBinary, HubTicket.TokenHashLength) { Value = tokenHash })
            .ToListAsync(cancellationToken);

        return rows is [var row] ? row.UserId : null;
    }

    /// <summary>The <c>OUTPUT</c> row of <see cref="TryConsumeSql"/>.</summary>
    private sealed class ConsumedTicketRow
    {
        public Guid UserId { get; init; }
    }
}
