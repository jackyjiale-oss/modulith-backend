using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.IntegrationTests.Persistence;

namespace TemplateName.IntegrationTests.Outbox;

public sealed class OutboxCleanupJobTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string JobName = "TestOutboxCleanup";
    private const string GaugeName = "platform.outbox.abandoned";
    private const string ContextTag = "Test";

    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    private DateTime Now => Factory.Time.GetUtcNow().UtcDateTime;

    private int MaxAttempts => Factory.Services.GetRequiredService<IOptions<OutboxOptions>>().Value.MaxAttempts;

    [Fact]
    public async Task Every_outbox_context_has_a_daily_cleanup_job_named_after_it()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetServices<RecurringJobRegistration>()
            .Select(registration => (IRecurringJob)scope.ServiceProvider.GetRequiredService(registration.JobType))
            .Where(job => job.Name.EndsWith("OutboxCleanup", StringComparison.Ordinal))
            .ToDictionary(job => job.Name, job => job.DefaultInterval);

        jobs.Keys.ShouldBe(["AuthOutboxCleanup", "SampleOutboxCleanup", JobName], ignoreOrder: true);
        jobs.Values.ShouldAllBe(interval => interval == TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task The_scheduler_runs_the_cleanup_job_registered_by_AddOutbox()
    {
        await SeedProcessedAsync(Now - SevenDays - TimeSpan.FromHours(1));
        var runner = Factory.Services.GetRequiredService<RecurringJobRunner>();

        (await runner.RunDueAsync(name => name == JobName, Ct)).ShouldBe(1);

        (await MessageIdsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Processed_messages_older_than_seven_days_go_with_their_consumers()
    {
        var old = await SeedProcessedAsync(Now - SevenDays - TimeSpan.FromDays(1), consumers: 3);
        var older = await SeedProcessedAsync(Now - SevenDays - TimeSpan.FromDays(30), consumers: 2);
        var recent = await SeedProcessedAsync(Now - TimeSpan.FromDays(1), consumers: 3);

        var result = await RunAsync();

        // The result counts messages, not their consumers.
        result.Processed.ShouldBe(2);
        var remaining = await MessageIdsAsync();
        remaining.ShouldBe([recent]);
        remaining.ShouldNotContain(old);
        remaining.ShouldNotContain(older);
        (await ConsumerMessageIdsAsync()).ShouldBe([recent, recent, recent]);
    }

    [Fact]
    public async Task A_message_processed_exactly_seven_days_ago_stays()
    {
        var exactly = await SeedProcessedAsync(Now - SevenDays, consumers: 1);

        (await RunAsync()).Processed.ShouldBe(0);

        (await MessageIdsAsync()).ShouldBe([exactly]);
        (await ConsumerMessageIdsAsync()).ShouldBe([exactly]);
    }

    [Fact]
    public async Task A_message_processed_one_second_before_the_seven_days_goes()
    {
        var justOver = await SeedProcessedAsync(Now - SevenDays - TimeSpan.FromSeconds(1), consumers: 1);
        var exactly = await SeedProcessedAsync(Now - SevenDays, consumers: 1);

        (await RunAsync()).Processed.ShouldBe(1);

        var remaining = await MessageIdsAsync();
        remaining.ShouldBe([exactly]);
        remaining.ShouldNotContain(justOver);
        (await ConsumerMessageIdsAsync()).ShouldBe([exactly]);
    }

    [Fact]
    public async Task Pending_and_abandoned_messages_stay_and_the_gauge_counts_the_abandoned()
    {
        var longAgo = Now - TimeSpan.FromDays(60);
        var pending = await SeedUnprocessedAsync(longAgo, attempts: 0, consumers: 1);
        var failing = await SeedUnprocessedAsync(longAgo, attempts: MaxAttempts - 1, consumers: 1);
        var abandoned = await SeedUnprocessedAsync(longAgo, attempts: MaxAttempts, consumers: 2);
        var abandonedBeyond = await SeedUnprocessedAsync(longAgo, attempts: MaxAttempts + 3);
        var processed = await SeedProcessedAsync(longAgo, consumers: 1);

        var result = await RunAsync();

        result.Processed.ShouldBe(1);
        var remaining = await MessageIdsAsync();
        remaining.ShouldBe([pending, failing, abandoned, abandonedBeyond], ignoreOrder: true);
        remaining.ShouldNotContain(processed);
        (await ConsumerMessageIdsAsync()).ShouldBe([pending, failing, abandoned, abandoned], ignoreOrder: true);
        AbandonedGauge()[ContextTag].ShouldBe(2);
    }

    [Fact]
    public async Task The_gauge_follows_the_abandoned_count_run_by_run()
    {
        await SeedUnprocessedAsync(Now, attempts: MaxAttempts);
        await RunAsync();
        AbandonedGauge()[ContextTag].ShouldBe(1);

        await SeedUnprocessedAsync(Now, attempts: MaxAttempts);
        await RunAsync();
        AbandonedGauge()[ContextTag].ShouldBe(2);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<OutboxMessage>().ExecuteDeleteAsync(Ct);
        }

        await RunAsync();
        AbandonedGauge()[ContextTag].ShouldBe(0);
    }

    [Fact]
    public async Task A_backlog_of_five_with_a_batch_of_two_drains_over_three_runs()
    {
        for (var index = 0; index < 5; index++)
        {
            await SeedProcessedAsync(Now - TimeSpan.FromDays(30), consumers: 2);
        }

        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await MessageIdsAsync()).Count.ShouldBe(3);
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await MessageIdsAsync()).Count.ShouldBe(1);
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(1);

        (await MessageIdsAsync()).ShouldBeEmpty();
        (await ConsumerMessageIdsAsync()).ShouldBeEmpty();
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(0);
    }

    [Fact]
    public async Task A_run_stops_after_the_most_batches_it_may_run()
    {
        for (var index = 0; index < 7; index++)
        {
            await SeedProcessedAsync(Now - TimeSpan.FromDays(30));
        }

        (await RunAsync(batchSize: 2, maxBatchesPerRun: 2)).Processed.ShouldBe(4);
        (await MessageIdsAsync()).Count.ShouldBe(3);

        // With room for more batches than there is work, the run ends when a batch comes back short.
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 100)).Processed.ShouldBe(3);
        (await MessageIdsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00:00:00")]
    public async Task Retention_zero_or_null_deletes_nothing(string? retention)
    {
        var old = await SeedProcessedAsync(Now - TimeSpan.FromDays(400), consumers: 1);
        await SeedUnprocessedAsync(Now, attempts: MaxAttempts);

        var result = await RunWithRetentionAsync(retention is null ? null : TimeSpan.Parse(retention, CultureInfo.InvariantCulture));

        result.Processed.ShouldBe(0);
        (await MessageIdsAsync()).Count.ShouldBe(2);
        (await ConsumerMessageIdsAsync()).ShouldBe([old]);

        // Dead letters are still reported when the cleanup is off.
        AbandonedGauge()[ContextTag].ShouldBe(1);
    }

    [Fact]
    public async Task Cleanup_predicate_uses_the_filtered_index()
    {
        // Enough rows on either side of the cutoff that a scan is not the obvious plan.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            for (var index = 0; index < 300; index++)
            {
                context.Add(NewMessage(Now - TimeSpan.FromDays(index % 20), processed: index % 3 != 0, attempts: 0));
            }

            await context.SaveChangesAsync(Ct);
        }

        await using var planScope = Factory.Services.CreateAsyncScope();
        var job = CreateJob(planScope, batchSize: 1000, maxBatchesPerRun: 100, SevenDays);
        var connection = planScope.ServiceProvider.GetRequiredService<TestDbContext>().Database.GetDbConnection();
        await connection.OpenAsync(Ct);

        // The job's own statement, compiled but not run: the plan is what the DELETE would do.
        string plan;
        try
        {
            await ExecuteAsync(connection, "SET SHOWPLAN_XML ON");
            await using var command = connection.CreateCommand();
            command.CommandText = $"DECLARE @Batch int = 1000; DECLARE @Cutoff datetime2(3) = '2025-12-25T00:00:00'; {job.DeleteSql}";
            plan = string.Concat(await ReadPlansAsync(command));
        }
        finally
        {
            await ExecuteAsync(connection, "SET SHOWPLAN_XML OFF");
        }

        plan.ShouldContain("IX_OutboxMessages_ProcessedAt");
        var outboxScans = XDocument.Parse(plan).Descendants()
            .Where(element => element.Name.LocalName == "RelOp" && (string?)element.Attribute("PhysicalOp") == "Clustered Index Scan")
            .Where(scan => scan.Descendants().Any(element => element.Name.LocalName == "Object" && (string?)element.Attribute("Table") == "[OutboxMessages]"))
            .ToList();
        outboxScans.ShouldBeEmpty("the cleanup must not scan the outbox table");
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<string>> ReadPlansAsync(DbCommand command)
    {
        var plans = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        do
        {
            while (await reader.ReadAsync(Ct))
            {
                plans.Add(reader.GetString(0));
            }
        }
        while (await reader.NextResultAsync(Ct));

        return plans;
    }

    /// <summary>The abandoned-messages gauge by its <c>context</c> tag, as the meter reports it now.</summary>
    private static Dictionary<string, long> AbandonedGauge()
    {
        var values = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, subscribed) =>
        {
            if (instrument.Meter.Name == RecurringJobMetrics.MeterName && instrument.Name == GaugeName)
            {
                subscribed.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "context")
                {
                    values[(string)tag.Value!] = value;
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return values;
    }

    private OutboxCleanupJob<TestDbContext> CreateJob(AsyncServiceScope scope, int batchSize, int maxBatchesPerRun, TimeSpan? retention)
        => new(
            scope.ServiceProvider.GetRequiredService<TestDbContext>(),
            Factory.Time,
            Options.Create(new JobsOptions { MaxBatchesPerRun = maxBatchesPerRun }),
            Options.Create(new RetentionOptions { BatchSize = batchSize, Outbox = retention }),
            Factory.Services.GetRequiredService<IOptions<OutboxOptions>>(),
            Factory.Services.GetRequiredService<OutboxAbandonedGauge>());

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

    private static OutboxMessage NewMessage(DateTime at, bool processed, int attempts) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = "Test.Event",
        Content = "{}",
        OccurredAt = at,
        ProcessedAt = processed ? at : null,
        AttemptCount = attempts,
    };

    private Task<Guid> SeedProcessedAsync(DateTime processedAt, int consumers = 0)
        => SeedAsync(processedAt, processed: true, attempts: 0, consumers);

    private Task<Guid> SeedUnprocessedAsync(DateTime occurredAt, int attempts, int consumers = 0)
        => SeedAsync(occurredAt, processed: false, attempts, consumers);

    private async Task<Guid> SeedAsync(DateTime at, bool processed, int attempts, int consumers)
    {
        var message = NewMessage(at, processed, attempts);
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        context.Add(message);
        for (var index = 0; index < consumers; index++)
        {
            context.Add(new OutboxMessageConsumer { OutboxMessageId = message.Id, Name = $"Test.Handler{index}", ProcessedAt = at });
        }

        await context.SaveChangesAsync(Ct);
        return message.Id;
    }

    private async Task<List<Guid>> MessageIdsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<OutboxMessage>().Select(message => message.Id).ToListAsync(Ct);
    }

    private async Task<List<Guid>> ConsumerMessageIdsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<OutboxMessageConsumer>()
            .Select(consumer => consumer.OutboxMessageId)
            .ToListAsync(Ct);
    }
}
