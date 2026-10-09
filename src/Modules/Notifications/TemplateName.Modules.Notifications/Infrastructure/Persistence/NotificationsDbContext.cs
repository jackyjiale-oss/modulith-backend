using Microsoft.EntityFrameworkCore;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// The Notifications module's write model and inbox, in schema <c>notify</c>. There is no outbox: nothing in this module raises a
/// domain event.
/// </summary>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options), IUnitOfWork
{
    internal const string Schema = "notify";

    public async Task<bool> SaveChangesUnlessInboxDuplicateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (Inbox<NotificationsDbContext>.IsDuplicate(exception))
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
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);
        modelBuilder.ApplyInbox();
    }
}
