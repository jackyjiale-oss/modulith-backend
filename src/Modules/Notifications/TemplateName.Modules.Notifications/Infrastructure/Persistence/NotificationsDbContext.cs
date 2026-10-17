using Microsoft.Data.SqlClient;
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

    public async Task<bool> SaveChangesUnlessConflictAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsConflict(exception))
        {
            // The save ran in one transaction, which was rolled back; drop what was tracked so the retry starts from fresh reads.
            ChangeTracker.Clear();
            return false;
        }
    }

    // SQL Server errors 2627 (primary key or unique constraint) and 2601 (duplicate key in a unique index), or a row that an update or
    // delete expected and did not find or whose row version changed.
    private static bool IsConflict(Exception exception)
        => exception is DbUpdateConcurrencyException
            || exception is DbUpdateException { InnerException: SqlException { Number: 2627 or 2601 } };

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyDefaultConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);
        modelBuilder.ApplyInbox();
    }
}
