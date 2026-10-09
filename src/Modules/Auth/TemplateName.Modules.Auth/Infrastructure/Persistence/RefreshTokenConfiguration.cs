using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TemplateName.Modules.Auth.Domain.Sessions;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    /// <summary>A SHA-256 hash: <c>varbinary(32)</c>.</summary>
    internal const int TokenHashLength = 32;

    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(token => token.Id);

        // The domain sets the id, so a token added to a loaded session by Rotate is inserted rather than taken for an existing row.
        builder.Property(token => token.Id).ValueGeneratedNever();
        builder.Property(token => token.TokenHash).HasMaxLength(TokenHashLength);

        builder.HasIndex(token => token.TokenHash).IsUnique();
    }
}
