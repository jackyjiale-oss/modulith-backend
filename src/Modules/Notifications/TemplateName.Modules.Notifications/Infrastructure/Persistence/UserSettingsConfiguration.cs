using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Notifications.Domain.Preferences;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="UserNotificationProfile"/> to <c>notify.UserSettings</c>. The table keeps the plan's name; the type could not
/// (naming rule G5 forbids the <c>Settings</c> suffix).
/// </summary>
internal sealed class UserSettingsConfiguration : IEntityTypeConfiguration<UserNotificationProfile>
{
    public void Configure(EntityTypeBuilder<UserNotificationProfile> builder)
    {
        builder.ToTable("UserSettings");

        // The user's id is the key, in a column named for what it is.
        builder.HasKey(profile => profile.Id);
        builder.Property(profile => profile.Id).HasColumnName("UserId").ValueGeneratedNever();
        builder.Ignore(profile => profile.UserId);

        builder.Property(profile => profile.RowVersion).IsRowVersion();

        // No window: both columns are null. The window itself is a value of two local times (QuietHours).
        builder.ComplexProperty(
            profile => profile.QuietHours,
            window =>
            {
                window.Property(quietHours => quietHours.Start).HasColumnName("QuietHoursStart");
                window.Property(quietHours => quietHours.End).HasColumnName("QuietHoursEnd");
            });
    }
}
