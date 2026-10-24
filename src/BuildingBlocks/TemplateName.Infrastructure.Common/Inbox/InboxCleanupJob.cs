using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.Infrastructure.Common.Inbox;

/// <summary>
/// Deletes the inbox rows of <typeparamref name="TContext"/> that were processed (<c>ProcessedAt</c>) more than <c>Retention:Inbox</c> ago.
/// A row is the proof that a consumer already handled an event, so the retention must outlast the redelivery of that event by the outbox:
/// the options refuse a value between zero and one day, and one that the outbox retries can outlast (<c>Retention:Inbox</c> against
/// <c>Outbox:MaxAttempts</c>), so a late retry never meets a deleted row (ADR 0028). Named <c>{Context}InboxCleanup</c> (the context's
/// name without <c>DbContext</c>), daily.
/// </summary>
internal sealed class InboxCleanupJob<TContext>(
    TContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
    where TContext : DbContext
{
    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {inbox} WHERE ProcessedAt < @Cutoff;";

    private static readonly string ContextName = ContextNameOf(typeof(TContext));

    private readonly TimeSpan? _retentionPeriod = retention.Value.Inbox;
    private string? _deleteSql;

    public override string Name => ContextName + "InboxCleanup";

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql
        => _deleteSql ??= DeleteSqlTemplate.Replace("{inbox}", DelimitedTable<InboxMessage>(), StringComparison.Ordinal);
}
