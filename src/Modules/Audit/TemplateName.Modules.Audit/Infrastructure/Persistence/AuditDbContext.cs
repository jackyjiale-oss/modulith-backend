using Microsoft.EntityFrameworkCore;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Audit.Application.Abstractions;

namespace TemplateName.Modules.Audit.Infrastructure.Persistence;

/// <summary>
/// The Audit module's trail and inbox, in schema <c>audit</c>. There is no outbox: nothing in this module raises a domain event, and no
/// entity here is audited.
/// </summary>
internal sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options), IUnitOfWork
{
    internal const string Schema = "audit";

    public async Task<bool> SaveChangesUnlessInboxDuplicateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (Inbox<AuditDbContext>.IsDuplicate(exception))
        {
            // The save ran in one transaction, which was rolled back; drop what was pending so a later save cannot repeat it.
            ChangeTracker.Clear();
            return false;
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyDefaultConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuditDbContext).Assembly);
        modelBuilder.ApplyInbox();
    }
}
