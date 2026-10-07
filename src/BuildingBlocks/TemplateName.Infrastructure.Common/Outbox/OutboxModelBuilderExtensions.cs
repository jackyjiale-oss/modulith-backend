using Microsoft.EntityFrameworkCore;

namespace TemplateName.Infrastructure.Common.Outbox;

public static class OutboxModelBuilderExtensions
{
    /// <summary>
    /// Maps the module's outbox tables, <c>OutboxMessages</c> and <c>OutboxMessageConsumers</c>, into the context's default schema.
    /// Call it from <c>OnModelCreating</c> after <c>HasDefaultSchema</c>; register the dispatcher with <c>AddOutbox</c>.
    /// </summary>
    public static ModelBuilder ApplyOutbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable("OutboxMessages");
            builder.HasKey(message => message.Id);
            builder.Property(message => message.Id).ValueGeneratedNever();
            builder.Property(message => message.Type).HasMaxLength(OutboxMessage.TypeMaxLength);
            builder.Property(message => message.Error).HasMaxLength(OutboxMessage.ErrorMaxLength);

            // Pending messages only: the dispatcher's claim query reads them in OccurredAt order.
            builder.HasIndex(message => message.OccurredAt).HasFilter("[ProcessedAt] IS NULL");
        });

        modelBuilder.Entity<OutboxMessageConsumer>(builder =>
        {
            builder.ToTable("OutboxMessageConsumers");
            builder.HasKey(consumer => new { consumer.OutboxMessageId, consumer.Name });
            builder.Property(consumer => consumer.Name).HasMaxLength(OutboxMessageConsumer.NameMaxLength);
        });

        return modelBuilder;
    }
}
