using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    /// <summary>The longest stored culture: a supported UI culture name such as <c>zh-Hans</c> (the same limit as <c>auth.Users.Locale</c>).</summary>
    internal const int CultureMaxLength = 16;

    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");
        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.Id).ValueGeneratedNever();

        builder.Property(notification => notification.TypeCode).HasMaxLength(NotificationCatalog.MaxCodeLength);
        builder.Property(notification => notification.Culture).HasMaxLength(CultureMaxLength);
        builder.Property(notification => notification.CorrelationId).HasMaxLength(Notification.MaxCorrelationIdLength);

        // JSON documents of unbounded size: the data is a handful of variables, the protected data the ciphertext of single-use links.
        builder.Property(notification => notification.Data).HasColumnType("nvarchar(max)");
        builder.Property(notification => notification.ProtectedData).HasColumnType("nvarchar(max)");

        // One notification per event, type and recipient: a second delivery of the same event, or a replay, cannot add rows.
        builder.HasIndex(notification => new { notification.SourceMessageId, notification.TypeCode, notification.RecipientUserId }).IsUnique();

        // The deliveries belong to the notification and go with it; the recipient is another module's user, referred to by id only.
        builder.HasMany(notification => notification.Deliveries).WithOne().HasForeignKey(delivery => delivery.NotificationId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(notification => notification.Deliveries).HasField("_deliveries").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(notification => notification.DomainEvents);
    }
}
