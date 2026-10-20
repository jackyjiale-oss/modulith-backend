using Microsoft.EntityFrameworkCore;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.Infrastructure.Common.Idempotency;

/// <summary>The building blocks' own tables, in schema <c>platform</c>: the idempotency keys and the recurring jobs' state.</summary>
internal sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    internal const string Schema = "platform";

    // Exact comparison: the default case-insensitive collation would make "abc" and "ABC" the same key.
    private const string KeyCollation = "Latin1_General_100_BIN2";

    internal DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    internal DbSet<JobStateRecord> JobStates => Set<JobStateRecord>();

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

            // The idempotency cleanup job deletes the rows past their expiry.
            builder.HasIndex(record => record.ExpiresAt);
        });

        modelBuilder.Entity<JobStateRecord>(builder =>
        {
            builder.ToTable("JobStates");
            builder.HasKey(state => state.Name);
            builder.Property(state => state.Name).HasMaxLength(JobStateRecord.MaxNameLength).ValueGeneratedNever();
            builder.Property(state => state.LastStatus).HasMaxLength(JobStateRecord.MaxStatusLength);
            builder.Property(state => state.LastError).HasMaxLength(JobStateRecord.MaxErrorLength);
            builder.Property(state => state.RowVersion).IsRowVersion();
        });
    }
}
