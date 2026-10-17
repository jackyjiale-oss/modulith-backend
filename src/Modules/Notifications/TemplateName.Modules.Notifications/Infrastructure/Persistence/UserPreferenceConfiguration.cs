using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Preferences;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class UserPreferenceConfiguration : IEntityTypeConfiguration<UserPreference>
{
    public void Configure(EntityTypeBuilder<UserPreference> builder)
    {
        builder.ToTable("UserPreferences");

        // One row per user, type and channel; the leading UserId serves the "all preferences of a user" read.
        builder.HasKey(preference => new { preference.UserId, preference.TypeCode, preference.Channel });
        builder.Property(preference => preference.TypeCode).HasMaxLength(NotificationCatalog.MaxCodeLength);
    }
}
