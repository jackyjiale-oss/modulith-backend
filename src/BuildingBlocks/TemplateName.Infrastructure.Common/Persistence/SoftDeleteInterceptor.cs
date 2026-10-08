using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TemplateName.Application.Common.Identity;
using TemplateName.SharedKernel;

namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>
/// Turns the deletion of an <see cref="ISoftDeletable"/> entity into an update that sets <c>IsDeleted</c>, <c>DeletedAt</c> and
/// <c>DeletedBy</c>. Only those three columns are written.
/// </summary>
/// <remarks>
/// NOTE: only the entity itself is converted. Owned or cascade-deleted dependents that EF marks Deleted alongside it are still
/// hard-deleted unless they are <see cref="ISoftDeletable"/> themselves; owned types cannot carry the soft-delete query filter.
/// </remarks>
internal sealed class SoftDeleteInterceptor(ICurrentUser currentUser, TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ConvertDeletes(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ConvertDeletes(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ConvertDeletes(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var userId = currentUser.UserId;

        // Materialized: changing an entry's state while enumerating the change tracker is not allowed.
        var deletedEntries = context.ChangeTracker.Entries<ISoftDeletable>().Where(entry => entry.State == EntityState.Deleted).ToList();
        foreach (var entry in deletedEntries)
        {
            // Unchanged first, so that setting the three values marks only them as modified and the entry becomes Modified.
            entry.State = EntityState.Unchanged;
            entry.Property(nameof(ISoftDeletable.IsDeleted)).CurrentValue = true;
            entry.Property(nameof(ISoftDeletable.DeletedAt)).CurrentValue = now;
            entry.Property(nameof(ISoftDeletable.DeletedBy)).CurrentValue = userId;
        }
    }
}
