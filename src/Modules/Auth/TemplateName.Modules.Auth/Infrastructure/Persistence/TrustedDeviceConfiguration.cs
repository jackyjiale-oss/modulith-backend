using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.TrustedDevices;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class TrustedDeviceConfiguration : IEntityTypeConfiguration<TrustedDevice>
{
    public void Configure(EntityTypeBuilder<TrustedDevice> builder)
    {
        builder.ToTable("TrustedDevices");
        builder.HasKey(device => device.Id);
        builder.Property(device => device.Id).ValueGeneratedNever();

        builder.Property(device => device.TokenHash).HasMaxLength(TrustedDevice.HashLength);
        builder.Property(device => device.DeviceName).HasMaxLength(UserSession.MaxDeviceNameLength);
        builder.Property(device => device.UserAgent).HasMaxLength(UserSession.MaxUserAgentLength);
        builder.Property(device => device.SecurityStamp).HasMaxLength(UserConfiguration.SecurityStampMaxLength);

        builder.HasOne<User>().WithMany().HasForeignKey(device => device.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(device => device.TokenHash).IsUnique();

        // The device list's sort, which always filters on the owner; the leading UserId also serves the foreign key.
        builder.HasIndex(device => new { device.UserId, device.CreatedAt, device.Id });

        builder.Ignore(device => device.DomainEvents);
    }
}
