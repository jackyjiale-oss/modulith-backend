using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Notifications.Domain.HubTickets;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class HubTicketConfiguration : IEntityTypeConfiguration<HubTicket>
{
    public void Configure(EntityTypeBuilder<HubTicket> builder)
    {
        builder.ToTable("HubTickets");

        // The key is the SHA-256 hash of the ticket, never the ticket (a database or log leak yields nothing usable).
        builder.HasKey(ticket => ticket.TokenHash);
        builder.Property(ticket => ticket.TokenHash).HasMaxLength(HubTicket.TokenHashLength).ValueGeneratedNever();
    }
}
