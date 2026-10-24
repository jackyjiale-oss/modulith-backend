using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Idempotency;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Sample.Endpoints;

namespace TemplateName.IntegrationTests.Idempotency;

public sealed class IdempotencyCleanupJobTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string JobName = "IdempotencyKeysCleanup";
    private const string SubmitRoute = "/api/v1/sample/leave-requests";
    private const string KeyHeader = "Idempotency-Key";
    private const string ReplayedHeader = "Idempotency-Replayed";
    private const string Key = "submit-leave-request-1";

    private DateTime Now => Factory.Time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task The_cleanup_job_is_registered_hourly()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetServices<RecurringJobRegistration>()
            .Select(registration => (IRecurringJob)scope.ServiceProvider.GetRequiredService(registration.JobType))
            .Where(job => job.Name == JobName)
            .ToList();

        jobs.ShouldHaveSingleItem().DefaultInterval.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task The_scheduler_runs_the_cleanup_job()
    {
        await SeedAsync("expired", expiresAt: Now - TimeSpan.FromMinutes(1));
        var runner = Factory.Services.GetRequiredService<RecurringJobRunner>();

        (await runner.RunDueAsync(name => name == JobName, Ct)).ShouldBe(1);

        (await KeysAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Expired_keys_go_unexpired_and_in_progress_keys_stay()
    {
        await SeedAsync("expired-completed", expiresAt: Now - TimeSpan.FromHours(3), completed: true);
        await SeedAsync("expired-in-progress", expiresAt: Now - TimeSpan.FromMinutes(10), completed: false);
        await SeedAsync("unexpired-completed", expiresAt: Now + TimeSpan.FromHours(3), completed: true);
        await SeedAsync("unexpired-in-progress", expiresAt: Now + TimeSpan.FromMinutes(4), completed: false);

        var result = await RunAsync();

        result.Processed.ShouldBe(2);
        (await KeysAsync()).ShouldBe(["unexpired-completed", "unexpired-in-progress"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_key_exactly_at_ExpiresAt_stays_and_one_second_later_goes()
    {
        await SeedAsync("exactly", expiresAt: Now);

        (await RunAsync()).Processed.ShouldBe(0);
        (await KeysAsync()).ShouldBe(["exactly"]);

        Factory.Time.Advance(TimeSpan.FromSeconds(1));

        (await RunAsync()).Processed.ShouldBe(1);
        (await KeysAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_key_of_two_users_is_two_rows()
    {
        await SeedAsync(Key, expiresAt: Now - TimeSpan.FromHours(1), scope: "first-user");
        await SeedAsync(Key, expiresAt: Now - TimeSpan.FromHours(1), scope: "second-user");

        (await RunAsync()).Processed.ShouldBe(2);

        (await KeysAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Backlog_drains_across_runs()
    {
        for (var index = 0; index < 5; index++)
        {
            await SeedAsync($"expired-{index}", expiresAt: Now - TimeSpan.FromDays(2));
        }

        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await KeysAsync()).Count.ShouldBe(3);
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(1);
        (await KeysAsync()).ShouldBeEmpty();
        (await RunAsync(batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(0);
    }

    [Fact]
    public async Task Retention_settings_do_not_switch_the_job_off()
    {
        await SeedAsync("expired", expiresAt: Now - TimeSpan.FromHours(1));

        // The job has no retention of its own: a Retention section that turns the other cleanups off leaves it running.
        var result = await RunAsync(retention: new RetentionOptions { Outbox = null, Inbox = TimeSpan.Zero });

        result.Processed.ShouldBe(1);
    }

    [Fact]
    public async Task Only_the_job_s_own_Enabled_setting_switches_it_off()
    {
        await SeedAsync("expired", expiresAt: Now - TimeSpan.FromHours(1));
        await using var disabled = Factory.WithWebHostBuilder(builder => builder.UseSetting($"Jobs:{JobName}:Enabled", "false"));
        var runner = disabled.Services.GetRequiredService<RecurringJobRunner>();

        (await runner.RunDueAsync(name => name == JobName, Ct)).ShouldBe(0);

        (await KeysAsync()).ShouldBe(["expired"]);
    }

    [Fact]
    public async Task A_session_that_defaults_to_NOCOUNT_ON_still_counts_the_deleted_rows()
    {
        await SeedAsync("first", expiresAt: Now - TimeSpan.FromHours(1));
        await SeedAsync("second", expiresAt: Now - TimeSpan.FromHours(1));

        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await context.Database.OpenConnectionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("SET NOCOUNT ON", Ct);

        // With the setting on, SQL Server reports no row counts at all unless the statement turns it off.
        (await CreateJob(scope, batchSize: 1000, maxBatchesPerRun: 100, new RetentionOptions()).RunAsync(Ct)).Processed.ShouldBe(2);
        (await KeysAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Request_with_a_deleted_key_runs_again()
    {
        await SignInAsync("sample.leave_request.create");
        var request = new SubmitLeaveRequestRequest(Guid.NewGuid(), new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 4), "Family trip");
        using var first = await PostAsync(request);
        using var replayed = await PostAsync(request);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        replayed.Headers.GetValues(ReplayedHeader).ShouldHaveSingleItem().ShouldBe("true");

        Factory.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        (await RunAsync()).Processed.ShouldBe(1);
        (await KeysAsync()).ShouldBeEmpty();
        await RenewAccessTokenAsync();
        using var again = await PostAsync(request);

        // The key is gone, so the request is a new one: not replayed, a second leave request, and the key is held again.
        again.StatusCode.ShouldBe(HttpStatusCode.Created);
        again.Headers.Contains(ReplayedHeader).ShouldBeFalse();
        again.Headers.Location.ShouldNotBe(first.Headers.Location);
        (await KeysAsync()).ShouldBe([Key]);
    }

    [Fact]
    public async Task A_stored_response_that_has_not_expired_still_replays_after_a_run()
    {
        await SignInAsync("sample.leave_request.create");
        var request = new SubmitLeaveRequestRequest(Guid.NewGuid(), new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 4), "Family trip");
        using var first = await PostAsync(request);

        Factory.Time.Advance(TimeSpan.FromHours(23));
        (await RunAsync()).Processed.ShouldBe(0);
        await RenewAccessTokenAsync();
        using var second = await PostAsync(request);

        second.Headers.GetValues(ReplayedHeader).ShouldHaveSingleItem().ShouldBe("true");
        second.Headers.Location.ShouldBe(first.Headers.Location);
    }

    [Fact]
    public async Task Cleanup_predicate_uses_the_ExpiresAt_index()
    {
        // Enough rows on either side of the cutoff that a scan is not the obvious plan.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            for (var index = 0; index < 300; index++)
            {
                context.Add(NewRecord($"key-{index}", Now + TimeSpan.FromHours(index % 20 - 10), completed: index % 3 != 0, scope: "user"));
            }

            await context.SaveChangesAsync(Ct);
        }

        await using var planScope = Factory.Services.CreateAsyncScope();
        var job = CreateJob(planScope, batchSize: 1000, maxBatchesPerRun: 100, new RetentionOptions());
        var connection = planScope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.GetDbConnection();

        // The job's own statement, compiled but not run: the plan is what the DELETE would do.
        var plan = await SqlExecutionPlan.OfDeleteAsync(connection, job.DeleteStatement, Ct);

        plan.ShouldContain("IX_IdempotencyKeys_ExpiresAt");
        SqlExecutionPlan.ClusteredIndexScansOf(plan, "[IdempotencyKeys]").ShouldBeEmpty("the cleanup must not scan the idempotency table");
    }

    private IdempotencyCleanupJob CreateJob(AsyncServiceScope scope, int batchSize, int maxBatchesPerRun, RetentionOptions retention)
    {
        retention.BatchSize = batchSize;
        return new(
            scope.ServiceProvider.GetRequiredService<PlatformDbContext>(),
            Factory.Time,
            Options.Create(new JobsOptions { MaxBatchesPerRun = maxBatchesPerRun }),
            Options.Create(retention));
    }

    private async Task<JobResult> RunAsync(int batchSize = 1000, int maxBatchesPerRun = 100, RetentionOptions? retention = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await CreateJob(scope, batchSize, maxBatchesPerRun, retention ?? new RetentionOptions()).RunAsync(Ct);
    }

    private async Task<HttpResponseMessage> PostAsync(SubmitLeaveRequestRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, SubmitRoute) { Content = JsonContent.Create(request) };
        message.Headers.Add(KeyHeader, Key);
        return await Client.SendAsync(message, Ct);
    }

    private static IdempotencyRecord NewRecord(string key, DateTime expiresAt, bool completed, string scope) => new()
    {
        Scope = scope,
        Key = key,
        RequestHash = new byte[IdempotencyRecord.RequestHashLength],
        StatusCode = completed ? 201 : null,
        CreatedAt = expiresAt - TimeSpan.FromHours(1),
        ExpiresAt = expiresAt,
        LockId = Guid.NewGuid(),
    };

    private async Task SeedAsync(string key, DateTime expiresAt, bool completed = true, string scope = "user")
    {
        await using var serviceScope = Factory.Services.CreateAsyncScope();
        var context = serviceScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        context.Add(NewRecord(key, expiresAt, completed, scope));
        await context.SaveChangesAsync(Ct);
    }

    private async Task<List<string>> KeysAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().IdempotencyKeys.AsNoTracking()
            .Select(record => record.Key)
            .ToListAsync(Ct);
    }
}
