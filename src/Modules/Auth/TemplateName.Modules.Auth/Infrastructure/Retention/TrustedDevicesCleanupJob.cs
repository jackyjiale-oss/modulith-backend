using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Domain.TrustedDevices;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.Modules.Auth.Infrastructure.Retention;

/// <summary>
/// Deletes the trusted devices that expired (<c>ExpiresAt</c>) or were revoked (<c>RevokedAt</c>) more than <c>Auth:Retention:TrustedDevices</c>
/// ago, so a device revoked long before it would have expired goes too. Named <c>AuthTrustedDevicesCleanup</c>, daily.
/// </summary>
internal sealed class TrustedDevicesCleanupJob(
    AuthDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<AuthRetentionOptions> authRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "AuthTrustedDevicesCleanup";

    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {table} WHERE ExpiresAt < @Cutoff OR RevokedAt < @Cutoff;";

    private readonly TimeSpan? _retentionPeriod = authRetention.Value.TrustedDevices;
    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql => _deleteSql ??= DeleteSqlTemplate.Replace("{table}", DelimitedTable<TrustedDevice>(), StringComparison.Ordinal);
}
