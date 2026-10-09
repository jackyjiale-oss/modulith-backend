using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

/// <summary>The Auth module's write model, outbox and Data Protection key ring, in schema <c>auth</c>.</summary>
internal sealed class AuthDbContext : DbContext, IUnitOfWork, IDataProtectionKeyContext
{
    internal const string Schema = "auth";

    private const int SqlUniqueConstraintViolation = 2627;
    private const int SqlUniqueIndexViolation = 2601;

    public AuthDbContext(DbContextOptions<AuthDbContext> options)
        : base(options)
    {
        // A soft delete must not take the children with it. Cascading at save time lets SoftDeleteInterceptor turn the delete into an
        // update first, so a soft-deleted user keeps its password history and role assignments. A child removed from its collection
        // is still deleted at once (orphan deletion is unaffected).
        ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
    }

    /// <summary>The Data Protection key ring (ADR 0017), written by <c>PersistKeysToDbContext</c>.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The save ran in one transaction, which was rolled back; drop what was pending so a later save cannot repeat it.
            ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<bool> SaveChangesUnlessDuplicateAsync(string uniqueIndexName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueIndexName);

        try
        {
            await SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqlException { Number: SqlUniqueIndexViolation or SqlUniqueConstraintViolation } sqlException
                && sqlException.Message.Contains($"'{uniqueIndexName}'", StringComparison.Ordinal))
        {
            // As for a concurrency conflict: the transaction was rolled back, so drop what was pending. SQL Server puts the index name in
            // quotes in its message, whatever the language of the server.
            ChangeTracker.Clear();
            return false;
        }
    }

    // Password history, role assignments and grants are only read through their soft-deletable owner, whose filter already hides
    // them, so EF's warning about a filtered principal with an unfiltered required dependent does not apply.
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.ConfigureWarnings(
            warnings => warnings.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyDefaultConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthDbContext).Assembly);
        modelBuilder.Entity<DataProtectionKey>().ToTable("DataProtectionKeys");
        modelBuilder.ApplyOutbox();
        modelBuilder.ApplySoftDeleteQueryFilters();
    }
}
