using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.IntegrationTests.Persistence;

namespace TemplateName.IntegrationTests.Inbox;

public sealed class InboxCleanupJobTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string JobName = "TestInboxCleanup";

    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    private DateTime Now => Factory.Time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task Every_inbox_context_has_a_daily_cleanup_job_named_after_it()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetServices<RecurringJobRegistration>()
            .Select(registration => (IRecurringJob)scope.ServiceProvider.GetRequiredService(registration.JobType))
            .Where(job => job.Name.EndsWith("InboxCleanup", StringComparison.Ordinal))
            .ToDictionary(job => job.Name, job => job.DefaultInterval);

        jobs.Keys.ShouldBe(["NotificationsInboxCleanup", "AuditInboxCleanup", JobName], ignoreOrder: true);
        jobs.Values.ShouldAllBe(interval => interval == TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task The_scheduler_runs_the_cleanup_job_registered_by_AddInbox()
    {
        await SeedAsync(Now - SevenDays - TimeSpan.FromHours(1));
        var runner = Factory.Services.GetRequiredService<RecurringJobRunner>();

        (await runner.RunDueAsync(name => name == JobName, Ct)).ShouldBe(1);

        (await MessageIdsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Rows_processed_more_than_seven_days_ago_go_and_newer_stay()
    {
        var old = await SeedAsync(Now - SevenDays - TimeSpan.FromDays(1));
        var older = await SeedAsync(Now - SevenDays - TimeSpan.FromDays(30));
        var recent = await SeedAsync(Now - TimeSpan.FromDays(1));
        var fresh = await SeedAsync(Now);

        var result = await RunAsync();

        result.Processed.ShouldBe(2);
        var remaining = await MessageIdsAsync();
        remaining.ShouldBe([recent, fresh], ignoreOrder: true);
        remaining.ShouldNotContain(old);
        remaining.ShouldNotContain(older);
    }

    [Fact]
    public async Task A_row_processed_exactly_seven_days_ago_stays_and_one_second_older_goes()
    {
        var exactly = await SeedAsync(Now - SevenDays);
        var justOver = await SeedAsync(Now - SevenDays - TimeSpan.FromSeconds(1));

        (await RunAsync()).Processed.ShouldBe(1);

        var remaining = await MessageIdsAsync();
        remaining.ShouldBe([exactly]);
        remaining.ShouldNotContain(justOver);
    }

    [Fact]
    public async Task One_event_processed_by_two_consumers_is_deleted_as_two_rows()
    {
        var messageId = await SeedAsync(Now - TimeSpan.FromDays(30), consumer: "TemplateName.Tests.FirstConsumer");
        await SeedAsync(Now - TimeSpan.FromDays(30), consumer: "TemplateName.Tests.SecondConsumer", messageId: messageId);

        (await RunAsync()).Processed.ShouldBe(2);

        (await MessageIdsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_row_inside_the_outbox_retry_window_is_never_deleted_even_at_the_minimum()
    {
        // One day is the shortest retention the options allow. A row deleted while the outbox can still redeliver its event would let the
        // consumer run a second time, so a row only hours old must stay however the retention is set.
        var twoHoursOld = await SeedAsync(Now - TimeSpan.FromHours(2));
        var twentyThreeHoursOld = await SeedAsync(Now - TimeSpan.FromHours(23));
        var overADay = await SeedAsync(Now - TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1));

        var result = await RunWithRetentionAsync(RetentionOptions.MinimumInbox);

        result.Processed.ShouldBe(1);
        var remaining = await MessageIdsAsync();
        remaining.ShouldBe([twoHoursOld, twentyThreeHoursOld], ignoreOrder: true);
        remaining.ShouldNotContain(overADay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00:00:00")]
    public async Task Retention_off_deletes_nothing(string? retention)
    {
        await SeedAsync(Now - TimeSpan.FromDays(400));
        await SeedAsync(Now - TimeSpan.FromDays(40));

        var result = await RunWithRetentionAsync(retention is null ? null : TimeSpan.Parse(retention, CultureInfo.InvariantCulture));

        result.Processed.ShouldBe(0);
        (await MessageIdsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Backlog_drains_across_runs()
    {
        for (var index = 0; index < 5; index++)
        {
            await SeedAsync(Now - TimeSpan.FromDays(30));
        }

        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await MessageIdsAsync()).Count.ShouldBe(3);
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await MessageIdsAsync()).Count.ShouldBe(1);
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(1);
        (await MessageIdsAsync()).ShouldBeEmpty();
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(0);
    }

    [Fact]
    public async Task A_run_stops_after_the_most_batches_it_may_run()
    {
        for (var index = 0; index < 7; index++)
        {
            await SeedAsync(Now - TimeSpan.FromDays(30));
        }

        (await RunAsync(batchSize: 2, maxBatchesPerRun: 2)).Processed.ShouldBe(4);
        (await MessageIdsAsync()).Count.ShouldBe(3);

        // With room for more batches than there is work, the run ends when a batch comes back short.
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 100)).Processed.ShouldBe(3);
        (await MessageIdsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_session_that_defaults_to_NOCOUNT_ON_still_counts_the_deleted_rows()
    {
        await SeedAsync(Now - TimeSpan.FromDays(30));
        await SeedAsync(Now - TimeSpan.FromDays(30));

        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        await context.Database.OpenConnectionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("SET NOCOUNT ON", Ct);

        // With the setting on, SQL Server reports no row counts at all unless the statement turns it off.
        (await CreateJob(scope, batchSize: 1000, maxBatchesPerRun: 100, SevenDays).RunAsync(Ct)).Processed.ShouldBe(2);
        (await MessageIdsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cleanup_predicate_uses_the_ProcessedAt_index()
    {
        // Enough rows on either side of the cutoff that a scan is not the obvious plan.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            for (var index = 0; index < 300; index++)
            {
                context.Add(NewRow(Guid.CreateVersion7(), "TemplateName.Tests.Consumer", Now - TimeSpan.FromDays(index % 20)));
            }

            await context.SaveChangesAsync(Ct);
        }

        await using var planScope = Factory.Services.CreateAsyncScope();
        var job = CreateJob(planScope, batchSize: 1000, maxBatchesPerRun: 100, SevenDays);
        var connection = planScope.ServiceProvider.GetRequiredService<TestDbContext>().Database.GetDbConnection();

        // The job's own statement, compiled but not run: the plan is what the DELETE would do.
        var plan = await SqlExecutionPlan.OfDeleteAsync(connection, job.DeleteStatement, Ct);

        plan.ShouldContain("IX_InboxMessages_ProcessedAt");
        SqlExecutionPlan.ClusteredIndexScansOf(plan, "[InboxMessages]").ShouldBeEmpty("the cleanup must not scan the inbox table");
    }

    private static InboxMessage NewRow(Guid messageId, string consumer, DateTime processedAt)
        => new() { MessageId = messageId, Consumer = consumer, ProcessedAt = processedAt };

    private InboxCleanupJob<TestDbContext> CreateJob(AsyncServiceScope scope, int batchSize, int maxBatchesPerRun, TimeSpan? retention)
        => new(
            scope.ServiceProvider.GetRequiredService<TestDbContext>(),
            Factory.Time,
            Options.Create(new JobsOptions { MaxBatchesPerRun = maxBatchesPerRun }),
            Options.Create(new RetentionOptions { BatchSize = batchSize, Inbox = retention }));

    private async Task<JobResult> RunAsync(int batchSize = 1000, int maxBatchesPerRun = 100)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await CreateJob(scope, batchSize, maxBatchesPerRun, SevenDays).RunAsync(Ct);
    }

    private async Task<JobResult> RunWithRetentionAsync(TimeSpan? retention)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await CreateJob(scope, batchSize: 1000, maxBatchesPerRun: 100, retention).RunAsync(Ct);
    }

    private async Task<Guid> SeedAsync(DateTime processedAt, string consumer = "TemplateName.Tests.Consumer", Guid? messageId = null)
    {
        var id = messageId ?? Guid.CreateVersion7();
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        context.Add(NewRow(id, consumer, processedAt));
        await context.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<List<Guid>> MessageIdsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<InboxMessage>()
            .Select(message => message.MessageId)
            .Distinct()
            .ToListAsync(Ct);
    }
}
