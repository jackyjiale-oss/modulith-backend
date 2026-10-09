using Microsoft.EntityFrameworkCore;

namespace TemplateName.Infrastructure.Common.Inbox;

public static class InboxModelBuilderExtensions
{
    /// <summary>
    /// Maps the module's inbox table, <c>InboxMessages</c> (primary key <c>MessageId</c>, <c>Consumer</c>), into the context's default
    /// schema. Call it from <c>OnModelCreating</c> after <c>HasDefaultSchema</c>; register <see cref="Inbox{TContext}"/> with
    /// <c>AddInbox</c>.
    /// </summary>
    public static ModelBuilder ApplyInbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<InboxMessage>(builder =>
        {
            builder.ToTable("InboxMessages");
            builder.HasKey(message => new { message.MessageId, message.Consumer }).HasName(InboxMessage.PrimaryKeyName);
            builder.Property(message => message.Consumer).HasMaxLength(InboxMessage.ConsumerMaxLength);
        });

        return modelBuilder;
    }
}
