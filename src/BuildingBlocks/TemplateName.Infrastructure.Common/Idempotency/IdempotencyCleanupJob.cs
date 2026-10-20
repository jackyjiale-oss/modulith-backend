using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.Infrastructure.Common.Idempotency;

/// <summary>
/// Deletes the rows of <c>platform.IdempotencyKeys</c> past their <c>ExpiresAt</c>: stored responses after <c>Idempotency:TimeToLive</c>
/// and in-progress leases that ran out. A row goes only when its <c>ExpiresAt</c> is before now, so one exactly at its expiry stays until
/// the next run. The middleware already treats an expired row as free and reclaims it when its key is used again, so deleting one changes
/// no outcome; it keeps stored responses (which may hold personal data) from outliving their time to live. Named
/// <c>IdempotencyKeysCleanup</c>, hourly. The expiry is in the row, so there is no retention to configure and no <c>Retention</c> value
/// turns the job off; <c>Jobs:IdempotencyKeysCleanup:Enabled</c> does.
/// </summary>
internal sealed class IdempotencyCleanupJob(
    PlatformDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {keys} WHERE ExpiresAt < @Cutoff;";

    private string? _deleteSql;

    public override string Name => "IdempotencyKeysCleanup";

    public override TimeSpan DefaultInterval => TimeSpan.FromHours(1);

    // Not read: CutoffAt replaces the retention. Zero would read as "off" in the base class otherwise.
    protected override TimeSpan? RetentionPeriod => TimeSpan.Zero;

    protected override string DeleteSql
        => _deleteSql ??= DeleteSqlTemplate.Replace("{keys}", DelimitedTable<IdempotencyRecord>(), StringComparison.Ordinal);

    protected override DateTime? CutoffAt(DateTime utcNow) => utcNow;
}
