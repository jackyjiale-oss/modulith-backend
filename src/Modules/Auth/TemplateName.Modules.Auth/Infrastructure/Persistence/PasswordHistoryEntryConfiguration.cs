using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class PasswordHistoryEntryConfiguration : IEntityTypeConfiguration<PasswordHistoryEntry>
{
    public void Configure(EntityTypeBuilder<PasswordHistoryEntry> builder)
    {
        builder.ToTable("PasswordHistory");
        builder.HasKey(entry => entry.Id);

        // The domain sets the id, so an entry appended to a loaded user is inserted rather than taken for an existing row.
        builder.Property(entry => entry.Id).ValueGeneratedNever();
        builder.Property(entry => entry.PasswordHash).HasMaxLength(UserConfiguration.PasswordHashMaxLength);
    }
}
