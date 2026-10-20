using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>
/// Deletes the outbox messages of <typeparamref name="TContext"/> that were published (<c>ProcessedAt</c>) more than
/// <c>Retention:Outbox</c> ago, with their <c>OutboxMessageConsumers</c> (no foreign key joins the two tables, so each batch deletes the
/// consumers of the messages it deletes in the same transaction). Pending messages and abandoned ones (<c>ProcessedAt IS NULL</c>) are
/// never touched; after every run, whether or not the retention is on, the job reports how many are abandoned in
/// <see cref="OutboxAbandonedGauge"/>. Named <c>{Context}OutboxCleanup</c> (the context's name without <c>DbContext</c>), daily.
/// </summary>
internal sealed class OutboxCleanupJob<TContext>(
    TContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<OutboxOptions> outbox,
    OutboxAbandonedGauge abandonedGauge)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
    where TContext : DbContext
{
    private const string DbContextSuffix = "DbContext";

    // One batch, one transaction: the messages and their consumers go together or not at all. The statement reports the rows of the
    // first DELETE only (the messages); NOCOUNT hides the consumers, so a batch that deleted fewer messages than @Batch is the last.
    private const string DeleteSqlTemplate = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
        DECLARE @Deleted TABLE (Id uniqueidentifier NOT NULL PRIMARY KEY);
        DELETE TOP (@Batch) FROM {outbox}
        OUTPUT deleted.Id INTO @Deleted
        WHERE ProcessedAt < @Cutoff;
        SET NOCOUNT ON;
        DELETE FROM {consumers} WHERE OutboxMessageId IN (SELECT Id FROM @Deleted);
        COMMIT TRANSACTION;
        SET NOCOUNT OFF;
        """;

    private static readonly string ContextName = ContextNameOf(typeof(TContext));

    private readonly TimeSpan? _retentionPeriod = retention.Value.Outbox;
    private string? _deleteSql;

    public override string Name => ContextName + "OutboxCleanup";

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    public override TimeSpan? RetentionPeriod => _retentionPeriod;

    public override string DeleteSql => _deleteSql ??= BuildDeleteSql();

    public override async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var result = await base.RunAsync(cancellationToken);

        var maxAttempts = outbox.Value.MaxAttempts;
        var abandoned = await Context.Set<OutboxMessage>()
            .CountAsync(message => message.ProcessedAt == null && message.AttemptCount >= maxAttempts, cancellationToken);
        abandonedGauge.Set(ContextName, abandoned);

        return result;
    }

    private static string ContextNameOf(Type contextType)
        => contextType.Name.Length > DbContextSuffix.Length && contextType.Name.EndsWith(DbContextSuffix, StringComparison.Ordinal)
            ? contextType.Name[..^DbContextSuffix.Length]
            : contextType.Name;

    private string BuildDeleteSql()
    {
        var helper = Context.GetService<ISqlGenerationHelper>();
        return DeleteSqlTemplate
            .Replace("{outbox}", DelimitedTable<OutboxMessage>(helper), StringComparison.Ordinal)
            .Replace("{consumers}", DelimitedTable<OutboxMessageConsumer>(helper), StringComparison.Ordinal);
    }

    private string DelimitedTable<TEntity>(ISqlGenerationHelper helper)
    {
        var entityType = Context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"{typeof(TContext).Name} does not map the outbox; call ApplyOutbox() in OnModelCreating.");
        return helper.DelimitIdentifier(entityType.GetTableName()!, entityType.GetSchema());
    }
}
