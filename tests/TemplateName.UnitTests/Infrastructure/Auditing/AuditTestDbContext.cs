using Microsoft.EntityFrameworkCore;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.SharedKernel;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.UnitTests.Infrastructure.Auditing;

/// <summary>
/// A context for the change extraction tests. It is never saved and never opens a connection: the tests track entities and call
/// <c>ChangeTracker.DetectChanges()</c> only.
/// </summary>
internal sealed class AuditTestDbContext : DbContext
{
    public DbSet<AuditedItem> Items => Set<AuditedItem>();

    public DbSet<AuditedLink> Links => Set<AuditedLink>();

    public DbSet<AuditedIdentity> Identities => Set<AuditedIdentity>();

    public DbSet<UnauditedItem> UnauditedItems => Set<UnauditedItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSqlServer("Server=unused;Database=unused");

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.ApplyDefaultConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditedItem>(builder =>
        {
            builder.HasKey(item => item.Id);
            builder.Property(item => item.Id).ValueGeneratedNever();
            builder.Property(item => item.State).HasConversion<string>().HasMaxLength(32);
            builder.Property(item => item.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<AuditedLink>(builder =>
        {
            builder.HasKey(link => new { link.LeftId, link.RightId });
            builder.Property(link => link.LeftId).ValueGeneratedNever();
            builder.Property(link => link.RightId).ValueGeneratedNever();
        });

        modelBuilder.Entity<AuditedIdentity>(builder =>
        {
            builder.HasKey(identity => identity.Id);
            builder.Property(identity => identity.Id).ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<UnauditedItem>(builder =>
        {
            builder.HasKey(item => item.Id);
            builder.Property(item => item.Id).ValueGeneratedNever();
        });
    }
}

internal enum AuditedItemState
{
    Draft,
    Submitted,
}

internal enum AuditedItemFlag
{
    None,
    Urgent,
}

[Audited]
internal sealed class AuditedItem : IAuditable, ISoftDeletable
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public int Count { get; set; }

    public DateOnly Due { get; set; }

    public AuditedItemState State { get; set; }

    public AuditedItemFlag Flag { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    [AuditIgnore]
    public string? Ignored { get; set; }

    [AuditMask]
    public string? Secret { get; set; }

    public byte[]? RowVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}

[Audited]
internal sealed class AuditedLink
{
    public Guid LeftId { get; set; }

    public int RightId { get; set; }

    public string Label { get; set; } = string.Empty;
}

[Audited]
internal sealed class AuditedIdentity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

internal sealed class UnauditedItem
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
