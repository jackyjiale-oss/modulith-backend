using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Audit.Domain.Trails;
using TemplateName.Modules.Audit.Infrastructure.Persistence;
using TemplateName.Modules.Audit.Infrastructure.Retention;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.IntegrationTests.Audit;

/// <summary>
/// The Audit trail cleanup job (ADR 0028): registered daily under its name, rows either side of the cutoff (a row goes only when strictly
/// older), a backlog larger than one batch draining over several runs, nothing deleted when the retention is null (the default) or zero,
/// and the plan of the job's statement using the <c>(OccurredAt, Id)</c> index.
/// </summary>
public sealed class AuditTrailsCleanupJobTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private static readonly TimeSpan OneYear = TimeSpan.FromDays(365);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    public static TheoryData<bool> OffValues => [true, false];

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task The_job_is_registered_daily_under_its_name()
    {
        await using var scope = Factory.Services.CreateAsyncScope();

        var job = scope.ServiceProvider.GetServices<RecurringJobRegistration>()
            .Select(registration => (IRecurringJob)scope.ServiceProvider.GetRequiredService(registration.JobType))
            .Single(candidate => candidate.Name == "AuditTrailsCleanup");

        job.DefaultInterval.ShouldBe(OneDay);
        AuditTrailsCleanupJob.JobName.ShouldBe("AuditTrailsCleanup");
    }

    [Fact]
    public async Task Trails_older_than_the_retention_go_and_a_row_exactly_at_the_cutoff_stays()
    {
        var exactly = await SeedTrailAsync(Now - OneYear);
        var justOver = await SeedTrailAsync(Now - OneYear - OneSecond);
        var recent = await SeedTrailAsync(Now - OneDay);

        var result = await RunAsync(OneYear);

        result.Processed.ShouldBe(1);
        var remaining = await TrailIdsAsync();
        remaining.ShouldBe([exactly, recent], ignoreOrder: true);
        remaining.ShouldNotContain(justOver);
    }

    [Fact]
    public async Task A_backlog_larger_than_one_batch_drains_over_several_runs()
    {
        for (var index = 0; index < 5; index++)
        {
            await SeedTrailAsync(Now - OneYear - OneDay);
        }

        var keeper = await SeedTrailAsync(Now - OneDay);

        (await RunAsync(OneYear, batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await TrailIdsAsync()).Count.ShouldBe(4);
        (await RunAsync(OneYear, batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await RunAsync(OneYear, batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(1);
        (await RunAsync(OneYear, batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(0);

        (await TrailIdsAsync()).ShouldBe([keeper]);
    }

    [Theory]
    [MemberData(nameof(OffValues))]
    public async Task A_null_or_zero_retention_deletes_nothing(bool zero)
    {
        await SeedTrailAsync(Now - TimeSpan.FromDays(4000));
        await SeedTrailAsync(Now - OneDay);

        var result = await RunAsync(zero ? TimeSpan.Zero : null);

        result.Processed.ShouldBe(0);
        (await TrailIdsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_host_keeps_every_trail_because_the_retention_is_off_by_default()
    {
        await SeedTrailAsync(Now - TimeSpan.FromDays(3650));
        await SeedTrailAsync(Now - OneDay);
        var runner = Factory.Services.GetRequiredService<RecurringJobRunner>();

        // The host's own configuration: the job is registered and runs, and deletes nothing.
        (await runner.RunDueAsync(name => name == AuditTrailsCleanupJob.JobName, Ct)).ShouldBe(1);

        (await TrailIdsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_cleanup_predicate_uses_the_OccurredAt_index()
    {
        // A table of mostly recent rows with an old tail of about a tenth of a per cent, as a real one is: the cutoff selects few rows, so a
        // seek on the index is the plan and scanning the table would cost more.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO [audit].[AuditTrails] ([ChangeSetId], [OccurredAt], [UserId], [SessionId], [Module], [EntityType], [EntityId], [Action], [Changes], [TraceId])
                SELECT NEWID(),
                    CASE WHEN n % 1000 = 0 THEN DATEADD(day, -800, {0}) ELSE DATEADD(minute, -CAST(n % 100000 AS int), {0}) END,
                    NEWID(), NULL, 'sample', 'LeaveRequest', CAST(n AS nvarchar(200)), 'Updated', '{{"Name":{{"new":"x"}}}}', NULL
                FROM (SELECT TOP (200000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS numbers;
                """,
                [Now.UtcDateTime],
                Ct);
        }

        await using var planScope = Factory.Services.CreateAsyncScope();
        var job = CreateJob(planScope, OneYear, batchSize: 1000, maxBatchesPerRun: 100);
        var connection = planScope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.GetDbConnection();

        // The job's own statement, compiled but not run, for the cutoff of the retention: the plan is what the DELETE would do.
        var plan = await SqlExecutionPlan.OfDeleteAsync(connection, job.DeleteStatement, (Now - OneYear).UtcDateTime, Ct);

        plan.ShouldContain("IX_AuditTrails_OccurredAt_Id");
        SqlExecutionPlan.ClusteredIndexScansOf(plan, "[AuditTrails]").ShouldBeEmpty("the cleanup must not scan the trail table");
    }

    private AuditTrailsCleanupJob CreateJob(AsyncServiceScope scope, TimeSpan? retention, int batchSize, int maxBatchesPerRun)
        => new(
            scope.ServiceProvider.GetRequiredService<AuditDbContext>(),
            Factory.Time,
            Options.Create(new JobsOptions { MaxBatchesPerRun = maxBatchesPerRun }),
            Options.Create(new RetentionOptions { BatchSize = batchSize }),
            Options.Create(new AuditRetentionOptions { Trails = retention }));

    private async Task<JobResult> RunAsync(TimeSpan? retention, int batchSize = 1000, int maxBatchesPerRun = 100)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await CreateJob(scope, retention, batchSize, maxBatchesPerRun).RunAsync(Ct);
    }

    private async Task<long> SeedTrailAsync(DateTimeOffset occurredAt)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var trail = AuditTrail.Create(
            Guid.NewGuid(),
            occurredAt,
            userId: null,
            sessionId: null,
            "sample",
            new EntityChange("LeaveRequest", "1", EntityChangeAction.Updated, """{"Name":{"new":"x"}}"""),
            traceId: null);
        context.Add(trail);
        await context.SaveChangesAsync(Ct);
        return trail.Id;
    }

    private async Task<List<long>> TrailIdsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Set<AuditTrail>().Select(trail => trail.Id).ToListAsync(Ct);
    }
}
