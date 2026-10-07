using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>
/// Dispatches the pending outbox messages of <typeparamref name="TContext"/> to their <see cref="IDomainEventHandler{TEvent}"/>s.
/// Several dispatchers (threads or app instances) can run at once: each claims a batch under a lease, so no transaction is held
/// while handlers run, and records every handler that succeeded, so a retry runs only the handlers that failed. A dispatcher
/// whose lease has run out starts no further message, and its final update is guarded by the lease it claimed, so it never
/// overwrites the outcome of the dispatcher that re-claimed the message. Delivery is at least once per handler and messages are
/// not ordered (ADR 0007).
/// </summary>
/// <remarks>
/// Handlers resolve the same scoped <typeparamref name="TContext"/> as the dispatcher. Changes a handler leaves tracked but unsaved
/// are committed in the same save as its consumer row, so they commit or roll back together; this is supported.
/// </remarks>
public sealed partial class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopeFactory,
    OutboxEventTypes<TContext> eventTypes,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher<TContext>> logger)
    where TContext : DbContext
{
    // UPDLOCK + READPAST: concurrent claims skip each other's rows instead of waiting or taking the same ones. The lease
    // (LockedUntil) keeps the claimed rows hidden after this statement commits; it expires if the claiming process dies.
    private const string ClaimSqlTemplate = """
        WITH batch AS (
            SELECT TOP (@BatchSize) *
            FROM {outbox} WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE ProcessedAt IS NULL AND AttemptCount < @MaxAttempts
              AND (NextAttemptAt IS NULL OR NextAttemptAt <= @Now)
              AND (LockedUntil IS NULL OR LockedUntil < @Now)
            ORDER BY OccurredAt)
        UPDATE batch SET LockedUntil = @LeaseUntil
        OUTPUT inserted.Id, inserted.Type, inserted.Content, inserted.AttemptCount, inserted.LockedUntil;
        """;

    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    private readonly OutboxOptions _options = options.Value;
    private string? _claimSql;

    /// <summary>
    /// Claims up to <see cref="OutboxOptions.BatchSize"/> due messages and dispatches each in its own scope, stopping early once the
    /// lease has run out (the rest are re-claimed later).
    /// </summary>
    /// <returns>The number of messages claimed.</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimBatchAsync(cancellationToken);

        foreach (var message in claimed)
        {
            // Past the lease another dispatcher may own these messages already; leave them untouched for it.
            if (timeProvider.GetUtcNow().UtcDateTime >= message.LockedUntil)
            {
                LogLeaseExpired(logger, message.Id);
                break;
            }

            await ProcessMessageAsync(message, cancellationToken);
        }

        return claimed.Count;
    }

    private async Task<List<ClaimedMessage>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        _claimSql ??= BuildClaimSql(context);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await context.Database
            .SqlQueryRaw<ClaimedMessage>(
                _claimSql,
                new SqlParameter("@BatchSize", SqlDbType.Int) { Value = _options.BatchSize },
                new SqlParameter("@MaxAttempts", SqlDbType.Int) { Value = _options.MaxAttempts },
                new SqlParameter("@Now", SqlDbType.DateTime2) { Scale = 3, Value = now },
                new SqlParameter("@LeaseUntil", SqlDbType.DateTime2) { Scale = 3, Value = now + _options.LeaseDuration })
            .ToListAsync(cancellationToken);
    }

    private async Task ProcessMessageAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        var failures = new List<DispatchFailure>();
        try
        {
            await DispatchAsync(scope.ServiceProvider, context, message, failures, cancellationToken);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            // The message itself cannot be dispatched: unknown type or unreadable content.
            failures.Add(new DispatchFailure(HandlerName: null, exception));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var claimedLease = message.LockedUntil;

        // Only the holder of the lease this dispatcher claimed may finish the message.
        var ownedRow = context.Set<OutboxMessage>()
            .Where(outboxMessage => outboxMessage.Id == message.Id && outboxMessage.LockedUntil == claimedLease);

        int updated;
        if (failures.Count == 0)
        {
            updated = await ownedRow.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(outboxMessage => outboxMessage.ProcessedAt, now)
                    .SetProperty(outboxMessage => outboxMessage.LockedUntil, (DateTime?)null),
                cancellationToken);
        }
        else
        {
            // While the lease guard holds, nobody has changed the row since the claim, so the increment below produces exactly
            // AttemptCount + 1 as claimed.
            var error = Truncate(failures[0].Exception.ToString(), OutboxMessage.ErrorMaxLength);
            var nextAttemptAt = OutboxRetryPolicy.NextAttemptAt(message.AttemptCount + 1, now, _options.MaxAttempts);
            updated = await ownedRow.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(outboxMessage => outboxMessage.AttemptCount, outboxMessage => outboxMessage.AttemptCount + 1)
                    .SetProperty(outboxMessage => outboxMessage.Error, error)
                    .SetProperty(outboxMessage => outboxMessage.NextAttemptAt, nextAttemptAt)
                    .SetProperty(outboxMessage => outboxMessage.LockedUntil, (DateTime?)null),
                cancellationToken);
        }

        if (updated == 0)
        {
            // Another dispatcher re-claimed the message after this lease ran out; its outcome stands.
            LogLeaseLost(logger, message.Id, failures.Count);
            return;
        }

        foreach (var failure in failures)
        {
            if (failure.HandlerName is null)
            {
                LogMessageFailed(logger, failure.Exception, message.Id, message.Type, message.AttemptCount + 1);
            }
            else
            {
                LogHandlerFailed(logger, failure.Exception, failure.HandlerName, message.Id, message.AttemptCount + 1);
            }
        }
    }

    /// <summary>Runs every handler that has not yet processed the message, adding each handler failure to <paramref name="failures"/>.</summary>
    private async Task DispatchAsync(
        IServiceProvider services,
        TContext context,
        ClaimedMessage message,
        List<DispatchFailure> failures,
        CancellationToken cancellationToken)
    {
        var eventType = eventTypes.Find(message.Type)
            ?? throw new InvalidOperationException($"Unknown outbox message type '{message.Type}'.");
        var domainEvent = JsonSerializer.Deserialize(message.Content, eventType)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has no content.");

        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
        var handleMethod = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!;

        var consumers = context.Set<OutboxMessageConsumer>();
        var processedBy = (await consumers
                .Where(consumer => consumer.OutboxMessageId == message.Id)
                .Select(consumer => consumer.Name)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var handler in services.GetServices(handlerType).OfType<object>())
        {
            var handlerName = handler.GetType().FullName!;
            if (processedBy.Contains(handlerName))
            {
                continue;
            }

            try
            {
                await (Task)handleMethod.Invoke(handler, BindingFlags.DoNotWrapExceptions, binder: null, [domainEvent, cancellationToken], culture: null)!;

                consumers.Add(new OutboxMessageConsumer
                {
                    OutboxMessageId = message.Id,
                    Name = handlerName,
                    ProcessedAt = timeProvider.GetUtcNow().UtcDateTime,
                });
                await context.SaveChangesAsync(cancellationToken);
                processedBy.Add(handlerName);
            }
            catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
            {
                // Drop whatever this run left tracked (a failed save rolled it back with the consumer row), so the next handler's
                // save does not commit it. The remaining handlers still run; the retry runs only the ones that failed.
                context.ChangeTracker.Clear();

                if (IsUniqueKeyViolation(exception) && await IsRecordedAsync(consumers, message.Id, handlerName, cancellationToken))
                {
                    // A dispatcher that re-claimed the message after this lease ran out recorded the handler first.
                    LogAlreadyHandled(logger, handlerName, message.Id);
                    processedBy.Add(handlerName);
                    continue;
                }

                failures.Add(new DispatchFailure(handlerName, exception));
            }
        }
    }

    private static string BuildClaimSql(TContext context)
    {
        var entityType = context.Model.FindEntityType(typeof(OutboxMessage))
            ?? throw new InvalidOperationException($"{typeof(TContext).Name} does not map the outbox; call ApplyOutbox() in OnModelCreating.");
        var outbox = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(entityType.GetTableName()!, entityType.GetSchema());

        return ClaimSqlTemplate.Replace("{outbox}", outbox, StringComparison.Ordinal);
    }

    private static Task<bool> IsRecordedAsync(
        DbSet<OutboxMessageConsumer> consumers,
        Guid outboxMessageId,
        string handlerName,
        CancellationToken cancellationToken)
        => consumers.AnyAsync(consumer => consumer.OutboxMessageId == outboxMessageId && consumer.Name == handlerName, cancellationToken);

    private static bool IsUniqueKeyViolation(Exception exception)
        => exception is DbUpdateException { InnerException: SqlException { Number: UniqueConstraintViolation or UniqueIndexViolation } };

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken)
        => exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox handler {HandlerName} failed on message {OutboxMessageId}, attempt {Attempt}")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string handlerName, Guid outboxMessageId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {OutboxMessageId} of type {OutboxMessageType} could not be dispatched, attempt {Attempt}")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid outboxMessageId, string outboxMessageType, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox lease expired before message {OutboxMessageId}; leaving the rest of the batch to be re-claimed")]
    private static partial void LogLeaseExpired(ILogger logger, Guid outboxMessageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox lease on message {OutboxMessageId} was lost to another dispatcher; its outcome stands ({FailureCount} local failures not recorded)")]
    private static partial void LogLeaseLost(ILogger logger, Guid outboxMessageId, int failureCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox handler {HandlerName} was already recorded for message {OutboxMessageId} by another dispatcher")]
    private static partial void LogAlreadyHandled(ILogger logger, string handlerName, Guid outboxMessageId);

    /// <summary>The columns the claim query returns; <see cref="LockedUntil"/> is the lease this dispatcher holds.</summary>
    private sealed record ClaimedMessage(Guid Id, string Type, string Content, int AttemptCount, DateTime LockedUntil);

    /// <summary>A handler failure, or a failure of the whole message when <see cref="HandlerName"/> is <see langword="null"/>.</summary>
    private sealed record DispatchFailure(string? HandlerName, Exception Exception);
}
