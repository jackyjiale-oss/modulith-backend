using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TemplateName.Application.Common.Identity;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.SharedKernel;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.Infrastructure.Common.Auditing;

/// <summary>
/// Records the audited changes of a save (<see cref="EntityChangeExtractor"/>) as one <see cref="EntityChangesRecordedDomainEvent"/>
/// in an <see cref="OutboxMessage"/> added to the same save, with the current user and session, the trace id, the time and the module
/// (the context's default schema). A save with no audited change adds no row. A context that has audited changes must map the outbox
/// (<c>ApplyOutbox</c>); otherwise its save fails, so an audited change is never saved without its record.
/// </summary>
/// <remarks>
/// A save that fails leaves the row tracked as Added next to the still-changed entities, so a retry of the save on the same context
/// would add a second row for the same changes. The interceptor therefore remembers the row it added for each context until the save
/// succeeds, and a retry replaces that row's content (or drops the row when nothing audited is left) instead of adding another. If
/// the context is discarded, the row goes with it.
/// </remarks>
internal sealed class AuditTrailInterceptor(ICurrentUser currentUser, TimeProvider timeProvider) : SaveChangesInterceptor
{
    // The audit row of each context's save that has not succeeded yet. Weak, so a discarded context takes its entry with it.
    private readonly ConditionalWeakTable<DbContext, OutboxMessage> _pendingMessages = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        RecordChanges(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        RecordChanges(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ForgetPendingMessage(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ForgetPendingMessage(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private void RecordChanges(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var changes = EntityChangeExtractor.Extract(context.ChangeTracker);
        var pendingMessage = FindPendingMessage(context);

        if (changes.Count == 0)
        {
            // A retry with nothing audited left: the earlier attempt's row would record changes this save does not make.
            if (pendingMessage is not null)
            {
                context.Entry(pendingMessage).State = EntityState.Detached;
                _pendingMessages.Remove(context);
            }

            return;
        }

        if (context.Model.FindEntityType(typeof(OutboxMessage)) is null)
        {
            throw new InvalidOperationException(
                $"{context.GetType().Name} saves audited entities but does not map the outbox; call ApplyOutbox() in OnModelCreating.");
        }

        var module = context.Model.GetDefaultSchema()
            ?? throw new InvalidOperationException(
                $"{context.GetType().Name} saves audited entities but has no default schema, which names the module of the changes.");

        var now = timeProvider.GetUtcNow();
        var changeSet = new EntityChangesRecordedDomainEvent(
            currentUser.UserId,
            currentUser.SessionId,
            Activity.Current?.TraceId.ToHexString(),
            module,
            changes);
        var content = JsonSerializer.Serialize(changeSet);

        if (pendingMessage is not null)
        {
            pendingMessage.Content = content;
            pendingMessage.OccurredAt = now.UtcDateTime;
            return;
        }

        var message = new OutboxMessage
        {
            Id = SequentialGuid.Create(now),
            Type = typeof(EntityChangesRecordedDomainEvent).FullName!,
            Content = content,
            OccurredAt = now.UtcDateTime,
        };
        context.Set<OutboxMessage>().Add(message);
        _pendingMessages.AddOrUpdate(context, message);
    }

    /// <summary>The row an earlier, failed save of <paramref name="context"/> added, if the context still tracks it as Added.</summary>
    private OutboxMessage? FindPendingMessage(DbContext context)
    {
        if (!_pendingMessages.TryGetValue(context, out var message))
        {
            return null;
        }

        // Cleared from the tracker since (ChangeTracker.Clear, or the entry detached): it will not be saved, so it is not pending.
        if (context.Entry(message).State != EntityState.Added)
        {
            _pendingMessages.Remove(context);
            return null;
        }

        return message;
    }

    private void ForgetPendingMessage(DbContext? context)
    {
        if (context is not null)
        {
            _pendingMessages.Remove(context);
        }
    }
}
