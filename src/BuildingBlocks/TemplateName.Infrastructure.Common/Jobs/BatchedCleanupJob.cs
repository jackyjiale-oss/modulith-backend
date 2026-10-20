using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>
/// A recurring job that deletes the rows of a table that are older than a retention, in batches (ADR 0028). A subclass names the job,
/// supplies the retention it reads from its options and the <c>DELETE TOP (@Batch)</c> statement; the base class computes the cutoff
/// (<c>now - retention</c>, so a row goes only when strictly older), runs the statement in batches of <c>Retention:BatchSize</c> rows and
/// stops after <c>Jobs:MaxBatchesPerRun</c> batches, so a backlog drains over several runs. The constructor of a subclass must be cheap and
/// open no connection: the scheduler resolves every job once at start to read its name and interval.
/// </summary>
public abstract class BatchedCleanupJob(
    DbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention) : IRecurringJob
{
    public abstract string Name { get; }

    public abstract TimeSpan DefaultInterval { get; }

    /// <summary>How long a row is kept; <see langword="null"/> or zero turns the job off (a run does nothing).</summary>
    protected abstract TimeSpan? RetentionPeriod { get; }

    /// <summary>
    /// The statement of one batch: a <c>DELETE TOP (@Batch)</c> of the rows whose age column is before <c>@Cutoff</c> (a <c>datetime2(3)</c>
    /// in UTC), which reports the number of rows it deleted (the first <c>DELETE</c>'s: a statement that also removes rows of a second table
    /// turns <c>NOCOUNT</c> on before it). Where a batch also removes rows of a second table, the batch is one transaction.
    /// </summary>
    protected abstract string DeleteSql { get; }

    /// <summary>The context the job deletes through, for a subclass that needs it as well.</summary>
    protected DbContext Context => context;

    /// <summary><see cref="DeleteSql"/> for the execution-plan tests, which compile the job's own statement.</summary>
    internal string DeleteStatement => DeleteSql;

    /// <summary>
    /// The name of <paramref name="contextType"/> without its <c>DbContext</c> suffix, which a job names itself and its metrics after
    /// (<c>NotificationsDbContext</c> is <c>Notifications</c>).
    /// </summary>
    protected static string ContextNameOf(Type contextType)
    {
        ArgumentNullException.ThrowIfNull(contextType);

        const string Suffix = "DbContext";
        return contextType.Name.Length > Suffix.Length && contextType.Name.EndsWith(Suffix, StringComparison.Ordinal)
            ? contextType.Name[..^Suffix.Length]
            : contextType.Name;
    }

    public virtual async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        if (CutoffAt(utcNow) is not { } cutoff)
        {
            return new JobResult(0);
        }

        var deleted = await BatchedDelete.RunAsync(
            context,
            DeleteSql,
            cutoff,
            retention.Value.BatchSize,
            jobs.Value.MaxBatchesPerRun,
            cancellationToken);

        return new JobResult(deleted);
    }

    /// <summary>
    /// The instant before which a row goes, or <see langword="null"/> when the job is off. By default <paramref name="utcNow"/> minus
    /// <see cref="RetentionPeriod"/>, and off when that is <see langword="null"/> or zero; a job whose rows carry their own expiry
    /// overrides it.
    /// </summary>
    protected virtual DateTime? CutoffAt(DateTime utcNow)
        => RetentionPeriod is { } period && period > TimeSpan.Zero ? utcNow - period : null;

    /// <summary>The quoted, schema-qualified table <typeparamref name="TEntity"/> is mapped to in the job's context, for <see cref="DeleteSql"/>.</summary>
    protected string DelimitedTable<TEntity>()
        where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"{context.GetType().Name} does not map {typeof(TEntity).Name}; check its OnModelCreating.");
        return context.GetService<ISqlGenerationHelper>().DelimitIdentifier(entityType.GetTableName()!, entityType.GetSchema());
    }
}
