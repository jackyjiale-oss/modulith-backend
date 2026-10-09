using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Notifications.Domain.Deliveries;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class DeliveryConfiguration : IEntityTypeConfiguration<Delivery>
{
    /// <summary>The filter of the worker index: <see cref="DeliveryStatus.Pending"/> (0), the only status the worker picks up.</summary>
    private const string PendingFilter = "[Status] = 0";

    public void Configure(EntityTypeBuilder<Delivery> builder)
    {
        builder.ToTable("Deliveries");
        builder.HasKey(delivery => delivery.Id);
        builder.Property(delivery => delivery.Id).ValueGeneratedNever();

        builder.Property(delivery => delivery.Destination).HasMaxLength(Delivery.MaxDestinationLength);
        builder.Property(delivery => delivery.LastError).HasMaxLength(Delivery.MaxLastErrorLength);
        builder.Property(delivery => delivery.RenderedSubject).HasMaxLength(Delivery.MaxRenderedSubjectLength);

        // The delivery worker's claim: the oldest due pending deliveries. Filtered to the pending rows, so it stays small however many
        // settled deliveries pile up, and covering the columns the claim reads besides the key.
        builder.HasIndex(delivery => new { delivery.Status, delivery.NextAttemptAt })
            .IncludeProperties(delivery => new { delivery.Channel, delivery.LockedUntil })
            .HasFilter(PendingFilter);

        // The administrator's list: newest first, paged by (CreatedAt, Id) (review P7).
        builder.HasIndex(delivery => new { delivery.CreatedAt, delivery.Id }).IsDescending(true, true);
    }
}
