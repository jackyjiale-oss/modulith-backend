using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class VerificationCodeConfiguration : IEntityTypeConfiguration<VerificationCode>
{
    public void Configure(EntityTypeBuilder<VerificationCode> builder)
    {
        builder.ToTable("VerificationCodes");
        builder.HasKey(code => code.Id);
        builder.Property(code => code.Id).ValueGeneratedNever();

        builder.Property(code => code.Target).HasMaxLength(UserConfiguration.EmailMaxLength);
        builder.Property(code => code.TokenHash).HasMaxLength(RefreshTokenConfiguration.TokenHashLength);
        builder.Property(code => code.CreatedIp).HasMaxLength(VerificationCode.MaxCreatedIpLength);

        builder.HasOne<User>().WithMany().HasForeignKey(code => code.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(code => code.TokenHash).IsUnique();

        // The pending-code and resend-cooldown lookups: one user, one purpose, newest first.
        builder.HasIndex(code => new { code.UserId, code.Purpose, code.CreatedAt });

        builder.Ignore(code => code.DomainEvents);
    }
}
