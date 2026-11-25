using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class PhoneVerificationCodeConfiguration : IEntityTypeConfiguration<PhoneVerificationCode>
{
    public void Configure(EntityTypeBuilder<PhoneVerificationCode> builder)
    {
        builder.ToTable("PhoneVerificationCodes");
        builder.HasKey(code => code.Id);
        builder.Property(code => code.Id).ValueGeneratedNever();

        builder.Property(code => code.PhoneNumber).HasMaxLength(User.MaxPhoneNumberLength);

        // tinyint, always written by the application. The migration that added it gave the rows already there the value 1 (Verify); the
        // model has no database default, so a purpose is never left to the database.
        builder.Property(code => code.Purpose);
        builder.Property(code => code.CodeHash).HasMaxLength(32);
        builder.Property(code => code.Salt).HasMaxLength(PhoneVerificationCode.SaltLength);

        builder.HasOne<User>().WithMany().HasForeignKey(code => code.UserId).OnDelete(DeleteBehavior.Restrict);

        // The pending code, the resend cooldown and the daily count: one user, newest first.
        builder.HasIndex(code => new { code.UserId, code.CreatedAt });

        // The cleanup job deletes the codes that expired before the retention cutoff.
        builder.HasIndex(code => code.ExpiresAt);

        builder.Ignore(code => code.DomainEvents);
    }
}
