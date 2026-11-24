using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.Modules.Auth.Infrastructure.Retention;

/// <summary>
/// Deletes the phone verification codes that expired (<c>ExpiresAt</c>) more than <c>Auth:Retention:PhoneVerificationCodes</c> ago, used
/// or not. Named <c>AuthPhoneVerificationCodesCleanup</c>, daily.
/// </summary>
internal sealed class PhoneVerificationCodesCleanupJob(
    AuthDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<AuthRetentionOptions> authRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "AuthPhoneVerificationCodesCleanup";

    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {table} WHERE ExpiresAt < @Cutoff;";

    private readonly TimeSpan? _retentionPeriod = authRetention.Value.PhoneVerificationCodes;
    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql =>
        _deleteSql ??= DeleteSqlTemplate.Replace("{table}", DelimitedTable<PhoneVerificationCode>(), StringComparison.Ordinal);
}
