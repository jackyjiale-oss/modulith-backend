using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    internal const int EmailMaxLength = User.MaxEmailLength;
    internal const int DisplayNameMaxLength = User.MaxDisplayNameLength;
    internal const int LocaleMaxLength = User.MaxLocaleLength;
    internal const int TimeZoneMaxLength = 64;
    internal const int PasswordHashMaxLength = 256;
    internal const int SecurityStampMaxLength = 64;

    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).ValueGeneratedNever();

        builder.Property(user => user.Email).HasMaxLength(EmailMaxLength);
        builder.Property(user => user.NormalizedEmail).HasMaxLength(EmailMaxLength);
        builder.Property(user => user.PasswordHash).HasMaxLength(PasswordHashMaxLength);
        builder.Property(user => user.SecurityStamp).HasMaxLength(SecurityStampMaxLength);
        builder.Property(user => user.DisplayName).HasMaxLength(DisplayNameMaxLength);
        builder.Property(user => user.Locale).HasMaxLength(LocaleMaxLength);
        builder.Property(user => user.TimeZone).HasMaxLength(TimeZoneMaxLength);
        builder.Property(user => user.RowVersion).IsRowVersion();

        // A soft-deleted user frees the address for a new account.
        builder.HasIndex(user => user.NormalizedEmail).IsUnique().HasFilter("[IsDeleted] = 0");

        builder.HasMany(user => user.PasswordHistory).WithOne().HasForeignKey(entry => entry.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(user => user.PasswordHistory).HasField("_passwordHistory").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(user => user.Roles).WithOne().HasForeignKey(assignment => assignment.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(user => user.Roles).HasField("_roles").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(user => user.DomainEvents);
    }
}
