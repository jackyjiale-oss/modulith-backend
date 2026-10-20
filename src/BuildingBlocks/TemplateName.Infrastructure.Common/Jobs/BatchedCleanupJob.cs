using Microsoft.EntityFrameworkCore;
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
    public abstract TimeSpan? RetentionPeriod { get; }

    /// <summary>
    /// The statement of one batch: a <c>DELETE TOP (@Batch)</c> of the rows whose age column is before <c>@Cutoff</c> (a <c>datetime2(3)</c>
    /// in UTC), which reports the number of rows it deleted. Where a batch also removes rows of a second table, the batch is one transaction.
    /// </summary>
    public abstract string DeleteSql { get; }

    /// <summary>The context the job deletes through, for a subclass that needs it as well.</summary>
    protected DbContext Context => context;

    public virtual async Task<JobResult> RunAsync(CancellationToken cancellationToken)
    {
        if (RetentionPeriod is not { } period || period <= TimeSpan.Zero)
        {
            return new JobResult(0);
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime - period;
        var deleted = await BatchedDelete.RunAsync(
            context,
            DeleteSql,
            cutoff,
            retention.Value.BatchSize,
            jobs.Value.MaxBatchesPerRun,
            cancellationToken);

        return new JobResult(deleted);
    }
}
