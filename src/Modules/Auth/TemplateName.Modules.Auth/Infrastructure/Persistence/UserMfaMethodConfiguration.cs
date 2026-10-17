using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class UserMfaMethodConfiguration : IEntityTypeConfiguration<UserMfaMethod>
{
    public void Configure(EntityTypeBuilder<UserMfaMethod> builder)
    {
        builder.ToTable("UserMfaMethods");
        builder.HasKey(method => method.Id);
        builder.Property(method => method.Id).ValueGeneratedNever();

        builder.Property(method => method.Label).HasMaxLength(UserMfaMethod.MaxLabelLength);
        builder.Property(method => method.SecretEncrypted).HasMaxLength(UserMfaMethod.MaxSecretEncryptedLength);

        // The method refers to its user by id only; users are soft-deleted, never removed.
        builder.HasOne<User>().WithMany().HasForeignKey(method => method.UserId).OnDelete(DeleteBehavior.Restrict);

        // One method of each type per user (D18): two simultaneous setups cannot both insert. Also the user's lookups and the foreign key.
        builder.HasIndex(method => new { method.UserId, method.Type }).IsUnique().HasDatabaseName(UniqueIndexNames.MfaMethodPerUserAndType);

        builder.Ignore(method => method.DomainEvents);
    }
}
