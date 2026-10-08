using Microsoft.EntityFrameworkCore;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.Infrastructure.Common.Idempotency;

/// <summary>The building blocks' own tables, in schema <c>platform</c>: today the idempotency keys.</summary>
internal sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    internal const string Schema = "platform";

    // Exact comparison: the default case-insensitive collation would make "abc" and "ABC" the same key.
    private const string KeyCollation = "Latin1_General_100_BIN2";

    internal DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyDefaultConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<IdempotencyRecord>(builder =>
        {
            builder.ToTable("IdempotencyKeys");
            builder.HasKey(record => new { record.Scope, record.Key });
            builder.Property(record => record.Scope).HasMaxLength(IdempotencyRecord.MaxScopeLength);
            builder.Property(record => record.Key).HasMaxLength(IdempotencyRecord.MaxKeyLength).UseCollation(KeyCollation);
            builder.Property(record => record.RequestHash).HasMaxLength(IdempotencyRecord.RequestHashLength);
            builder.Property(record => record.ContentType).HasMaxLength(IdempotencyRecord.MaxContentTypeLength);
            builder.Property(record => record.Location).HasMaxLength(IdempotencyRecord.MaxLocationLength);
        });
    }
}
