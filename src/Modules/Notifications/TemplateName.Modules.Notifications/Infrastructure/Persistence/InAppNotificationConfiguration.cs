using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.InApp;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class InAppNotificationConfiguration : IEntityTypeConfiguration<InAppNotification>
{
    public void Configure(EntityTypeBuilder<InAppNotification> builder)
    {
        builder.ToTable("InAppNotifications");

        // The key is the delivery's id (a retried delivery cannot insert a second row). The row refers to its user and notification by id
        // only: the user is another module's, and the notification is read through the delivery that made the row.
        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.Id).ValueGeneratedNever();

        builder.Property(notification => notification.TypeCode).HasMaxLength(NotificationCatalog.MaxCodeLength);
        builder.Property(notification => notification.Title).HasMaxLength(InAppNotification.MaxTitleLength);
        builder.Property(notification => notification.Body).HasMaxLength(InAppNotification.MaxBodyLength);

        // The inbox list: one user's notifications, newest first, paged by (CreatedAt, Id) (review P7).
        builder.HasIndex(notification => new { notification.UserId, notification.CreatedAt, notification.Id }).IsDescending(false, true, true);

        // The unread count and mark-all-read touch only unread rows, which are few.
        builder.HasIndex(notification => notification.UserId).HasFilter("[ReadAt] IS NULL");
    }
}
