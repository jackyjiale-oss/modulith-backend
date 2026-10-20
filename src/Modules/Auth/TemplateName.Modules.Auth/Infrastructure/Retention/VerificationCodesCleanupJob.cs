using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.Modules.Auth.Infrastructure.Retention;

/// <summary>
/// Deletes the verification codes that expired (<c>ExpiresAt</c>) more than <c>Auth:Retention:VerificationCodes</c> ago, whether or not they were
/// consumed (a consumed code expires on schedule like any other). Named <c>AuthVerificationCodesCleanup</c>, hourly.
/// </summary>
internal sealed class VerificationCodesCleanupJob(
    AuthDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<AuthRetentionOptions> authRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "AuthVerificationCodesCleanup";

    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {table} WHERE ExpiresAt < @Cutoff;";

    private readonly TimeSpan? _retentionPeriod = authRetention.Value.VerificationCodes;
    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromHours(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql => _deleteSql ??= DeleteSqlTemplate.Replace("{table}", DelimitedTable<VerificationCode>(), StringComparison.Ordinal);
}
