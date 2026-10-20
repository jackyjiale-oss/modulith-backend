using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class MfaChallengeConfiguration : IEntityTypeConfiguration<MfaChallenge>
{
    public void Configure(EntityTypeBuilder<MfaChallenge> builder)
    {
        builder.ToTable("MfaChallenges");
        builder.HasKey(challenge => challenge.Id);
        builder.Property(challenge => challenge.Id).ValueGeneratedNever();

        builder.Property(challenge => challenge.TokenHash).HasMaxLength(MfaChallenge.HashLength);
        builder.Property(challenge => challenge.SecurityStamp).HasMaxLength(UserConfiguration.SecurityStampMaxLength);
        builder.Property(challenge => challenge.DeviceName).HasMaxLength(UserSession.MaxDeviceNameLength);

        builder.HasOne<User>().WithMany().HasForeignKey(challenge => challenge.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(challenge => challenge.TokenHash).IsUnique();
        builder.HasIndex(challenge => challenge.UserId);

        // The cleanup job deletes the challenges past their expiry.
        builder.HasIndex(challenge => challenge.ExpiresAt);

        builder.Ignore(challenge => challenge.DomainEvents);
    }
}
