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
/// while handlers run, and records every handler that succeeded, so a retry runs only the handlers that failed. Delivery is
/// at least once per handler (ADR 0007).
/// </summary>
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
        OUTPUT inserted.Id, inserted.Type, inserted.Content, inserted.AttemptCount;
        """;

    private readonly OutboxOptions _options = options.Value;
    private string? _claimSql;

    /// <summary>Claims up to <see cref="OutboxOptions.BatchSize"/> due messages and dispatches each in its own scope.</summary>
    /// <returns>The number of messages claimed.</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimBatchAsync(cancellationToken);

        foreach (var message in claimed)
        {
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
                new SqlParameter("@Now", SqlDbType.DateTime2) { Precision = 3, Value = now },
                new SqlParameter("@LeaseUntil", SqlDbType.DateTime2) { Precision = 3, Value = now + _options.LeaseDuration })
            .ToListAsync(cancellationToken);
    }

    private async Task ProcessMessageAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var attempt = message.AttemptCount + 1;

        Exception? failure;
        try
        {
            failure = await DispatchAsync(scope.ServiceProvider, context, message, attempt, cancellationToken);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            // The message itself cannot be dispatched: unknown type or unreadable content.
            LogMessageFailed(logger, exception, message.Id, message.Type, attempt);
            failure = exception;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = context.Set<OutboxMessage>().Where(outboxMessage => outboxMessage.Id == message.Id);

        if (failure is null)
        {
            await row.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(outboxMessage => outboxMessage.ProcessedAt, now)
                    .SetProperty(outboxMessage => outboxMessage.LockedUntil, (DateTime?)null),
                cancellationToken);
            return;
        }

        var error = Truncate(failure.ToString(), OutboxMessage.ErrorMaxLength);
        var nextAttemptAt = OutboxRetryPolicy.NextAttemptAt(attempt, now, _options.MaxAttempts);
        await row.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(outboxMessage => outboxMessage.AttemptCount, attempt)
                .SetProperty(outboxMessage => outboxMessage.Error, error)
                .SetProperty(outboxMessage => outboxMessage.NextAttemptAt, nextAttemptAt)
                .SetProperty(outboxMessage => outboxMessage.LockedUntil, (DateTime?)null),
            cancellationToken);
    }

    /// <summary>Runs every handler that has not yet processed the message; returns the first handler failure, if any.</summary>
    private async Task<Exception?> DispatchAsync(
        IServiceProvider services,
        TContext context,
        ClaimedMessage message,
        int attempt,
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

        Exception? failure = null;
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
                // Drop whatever the failed handler left tracked, so the next handler's save does not commit it. The remaining
                // handlers still run; the retry runs only the ones that failed.
                context.ChangeTracker.Clear();
                LogHandlerFailed(logger, exception, handlerName, message.Id, attempt);
                failure ??= exception;
            }
        }

        return failure;
    }

    private static string BuildClaimSql(TContext context)
    {
        var entityType = context.Model.FindEntityType(typeof(OutboxMessage))
            ?? throw new InvalidOperationException($"{typeof(TContext).Name} does not map the outbox; call ApplyOutbox() in OnModelCreating.");
        var outbox = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(entityType.GetTableName()!, entityType.GetSchema());

        return ClaimSqlTemplate.Replace("{outbox}", outbox, StringComparison.Ordinal);
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken)
        => exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox handler {HandlerName} failed on message {OutboxMessageId}, attempt {Attempt}")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string handlerName, Guid outboxMessageId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {OutboxMessageId} of type {OutboxMessageType} could not be dispatched, attempt {Attempt}")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid outboxMessageId, string outboxMessageType, int attempt);

    /// <summary>The columns the claim query returns.</summary>
    private sealed record ClaimedMessage(Guid Id, string Type, string Content, int AttemptCount);
}
