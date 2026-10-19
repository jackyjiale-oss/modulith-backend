using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TemplateName.Infrastructure.Common.Inbox;

/// <summary>
/// The inbox of <typeparamref name="TContext"/> (ADR 0018): which integration events each consumer has processed. A consumer checks
/// <see cref="HasProcessedAsync"/>, writes its rows, calls <see cref="Record"/> and saves its context once, so the inbox row commits or
/// rolls back with its own rows. When two deliveries of one event race past the check, the second save fails on the inbox key:
/// <see cref="IsDuplicate"/> tells that failure apart, and the consumer drops its tracked changes and treats the event as processed.
/// </summary>
/// <typeparam name="TContext">The consumer module's context; it must call <c>ApplyInbox()</c>.</typeparam>
public sealed class Inbox<TContext>(TContext context, TimeProvider timeProvider)
    where TContext : DbContext
{
    // Primary key or unique constraint violation, and duplicate key in a unique index.
    private const int SqlUniqueConstraintViolation = 2627;
    private const int SqlUniqueIndexViolation = 2601;

    /// <summary>Whether <paramref name="consumer"/> has a saved record of <paramref name="messageId"/>.</summary>
    public Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken)
    {
        ValidateConsumer(consumer);

        return context.Set<InboxMessage>()
            .AnyAsync(message => message.MessageId == messageId && message.Consumer == consumer, cancellationToken);
    }

    /// <summary>
    /// Stages the record that <paramref name="consumer"/> has processed <paramref name="messageId"/>. Nothing is written until the
    /// caller saves the context, together with its own changes.
    /// </summary>
    public void Record(Guid messageId, string consumer)
    {
        ValidateConsumer(consumer);

        context.Set<InboxMessage>().Add(new InboxMessage
        {
            MessageId = messageId,
            Consumer = consumer,
            ProcessedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
    }

    /// <summary>
    /// Whether a failed save was refused because the inbox already holds the record (SQL Server error 2627 or 2601 on the inbox's
    /// primary key), as opposed to any other failure, including a unique key of the consumer's own tables. The save was rolled back.
    /// </summary>
    public static bool IsDuplicate(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // SQL Server quotes the constraint name in its message, whatever the language of the server.
        return exception.InnerException is SqlException { Number: SqlUniqueConstraintViolation or SqlUniqueIndexViolation } sqlException
            && sqlException.Message.Contains($"'{InboxMessage.PrimaryKeyName}'", StringComparison.Ordinal);
    }

    private static void ValidateConsumer(string consumer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(consumer.Length, InboxMessage.ConsumerMaxLength, nameof(consumer));
    }
}
