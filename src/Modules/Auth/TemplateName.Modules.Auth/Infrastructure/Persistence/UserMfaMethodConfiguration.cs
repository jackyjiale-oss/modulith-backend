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

        // Concurrency tokens: every UPDATE or DELETE of a tracked method also requires the secret and the confirmation it was loaded
        // with, so a confirm cannot confirm a secret a simultaneous setup replaced, and a setup cannot replace the secret of a method a
        // simultaneous confirm turned on; the loser's save throws DbUpdateConcurrencyException (409 concurrency.conflict). Every new
        // writer of this table through the change tracker keeps them. LastUsedTimeStep stays out: it changes only through
        // TryClaimTimeStepAsync's conditional UPDATE, which bypasses the tracker (D20).
        builder.Property(method => method.SecretEncrypted).HasMaxLength(UserMfaMethod.MaxSecretEncryptedLength).IsConcurrencyToken();
        builder.Property(method => method.ConfirmedAt).IsConcurrencyToken();

        // The method refers to its user by id only; users are soft-deleted, never removed.
        builder.HasOne<User>().WithMany().HasForeignKey(method => method.UserId).OnDelete(DeleteBehavior.Restrict);

        // One method of each type per user (D18): two simultaneous setups cannot both insert. Also the user's lookups and the foreign key.
        builder.HasIndex(method => new { method.UserId, method.Type }).IsUnique().HasDatabaseName(UniqueIndexNames.MfaMethodPerUserAndType);

        builder.Ignore(method => method.DomainEvents);
    }
}
