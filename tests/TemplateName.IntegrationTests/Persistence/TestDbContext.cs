using Microsoft.EntityFrameworkCore;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.IntegrationTests.Persistence;

/// <summary>
/// A module-style context (schema <c>test</c>) used to test the shared persistence conventions, the audit trail, the outbox and the
/// inbox. Created with <c>EnsureCreated</c>, not migrations.
/// </summary>
internal sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    internal const string Schema = "test";

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyDefaultConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<TestAggregate>(builder =>
        {
            builder.ToTable("TestAggregates");
            builder.HasKey(aggregate => aggregate.Id);
            builder.Property(aggregate => aggregate.Id).ValueGeneratedNever();
            builder.Property(aggregate => aggregate.Name).HasMaxLength(100);
            builder.Ignore(aggregate => aggregate.DomainEvents);
        });

        modelBuilder.Entity<AuditedTestAggregate>(builder =>
        {
            builder.ToTable("AuditedTestAggregates");
            builder.HasKey(aggregate => aggregate.Id);

            // An audited entity's key is known before the save, so its Created change carries it.
            builder.Property(aggregate => aggregate.Id).ValueGeneratedNever();
            builder.Property(aggregate => aggregate.Title).HasMaxLength(100);
            builder.Property(aggregate => aggregate.RowVersion).IsRowVersion();
        });

        modelBuilder.ApplyOutbox();
        modelBuilder.ApplyInbox();
        modelBuilder.ApplySoftDeleteQueryFilters();
    }
}
