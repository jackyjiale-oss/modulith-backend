using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class PushDeviceConfiguration : IEntityTypeConfiguration<PushDevice>
{
    internal const string ActiveFilter = "[DeactivatedAt] IS NULL";

    private const int TokenHashLength = 32;

    public void Configure(EntityTypeBuilder<PushDevice> builder)
    {
        builder.ToTable("PushDevices");
        builder.HasKey(device => device.Id);
        builder.Property(device => device.Id).ValueGeneratedNever();
        builder.Property(device => device.TokenHash).HasMaxLength(TokenHashLength);
        builder.Property(device => device.Token).HasMaxLength(PushDevice.MaxTokenLength);
        builder.Property(device => device.DeviceName).HasMaxLength(PushDevice.MaxDeviceNameLength);
        builder.Property(device => device.AppVersion).HasMaxLength(PushDevice.MaxAppVersionLength);
        builder.Ignore(device => device.IsActive);

        // A registration is matched by the token's hash (the token itself is too long to index); one row per token.
        builder.HasIndex(device => device.TokenHash).IsUnique();

        // The scheduler reads a user's active devices.
        builder.HasIndex(device => device.UserId).HasFilter(ActiveFilter);

        // The cleanup job deletes devices deactivated before its cutoff.
        builder.HasIndex(device => device.DeactivatedAt);
    }
}
