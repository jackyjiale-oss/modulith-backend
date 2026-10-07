using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.IntegrationTests.Persistence;

namespace TemplateName.IntegrationTests.Outbox;

public sealed class OutboxTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private readonly List<AsyncServiceScope> _scopes = [];

    private DateTime Now => Factory.Time.GetUtcNow().UtcDateTime;

    private OutboxDispatcher<TestDbContext> Dispatcher => Factory.Services.GetRequiredService<OutboxDispatcher<TestDbContext>>();

    [Fact]
    public async Task Saving_aggregate_writes_event_to_outbox_in_same_save()
    {
        var aggregate = TestAggregate.Create("a", Factory.Time.GetUtcNow());

        await using (var db = NewTestDbContext())
        {
            db.Add(aggregate);

            // One save writes the aggregate and its outbox row.
            (await db.SaveChangesAsync(Ct)).ShouldBe(2);
        }

        aggregate.DomainEvents.ShouldBeEmpty();
        var message = await SingleMessageAsync();
        message.Type.ShouldBe(typeof(TestAggregateCreatedDomainEvent).FullName);
        message.OccurredAt.ShouldBe(Now);
        message.ProcessedAt.ShouldBeNull();
        JsonSerializer.Deserialize<TestAggregateCreatedDomainEvent>(message.Content).ShouldBe(new TestAggregateCreatedDomainEvent(aggregate.Id));
    }

    [Fact]
    public async Task ProcessBatch_dispatches_and_marks_processed()
    {
        var aggregate = await SaveNewAggregateAsync("a");

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        Factory.EventRecorder.Events.ShouldHaveSingleItem().ShouldBe(new TestAggregateCreatedDomainEvent(aggregate.Id));
        var message = await SingleMessageAsync();
        message.ProcessedAt.ShouldBe(Now);
        message.LockedUntil.ShouldBeNull();
        message.AttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task Failing_handler_is_retried_and_successful_handler_is_not_rerun()
    {
        await SaveNewAggregateAsync("a");
        Factory.FlakySwitch.FailNext = true;

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        var failed = await SingleMessageAsync();
        failed.AttemptCount.ShouldBe(1);
        failed.Error.ShouldNotBeNull();
        failed.NextAttemptAt.ShouldBe(Now.AddSeconds(5));
        failed.LockedUntil.ShouldBeNull();
        failed.ProcessedAt.ShouldBeNull();
        Factory.EventRecorder.Events.Count.ShouldBe(1);

        // Not due yet.
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);

        Factory.Time.Advance(TimeSpan.FromSeconds(5));
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        var processed = await SingleMessageAsync();
        processed.ProcessedAt.ShouldBe(Now);
        processed.AttemptCount.ShouldBe(1);

        // The recording handler succeeded on the first attempt, so the retry skipped it.
        Factory.EventRecorder.Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Message_is_abandoned_after_max_attempts()
    {
        await SaveNewAggregateAsync("a");
        Factory.FlakySwitch.FailAlways = true;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
            Factory.Time.Advance(TimeSpan.FromHours(1));
        }

        var abandoned = await SingleMessageAsync();
        abandoned.AttemptCount.ShouldBe(5);
        abandoned.NextAttemptAt.ShouldBeNull();
        abandoned.ProcessedAt.ShouldBeNull();
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_dispatchers_process_each_message_once()
    {
        var ids = new List<Guid>();
        await using (var db = NewTestDbContext())
        {
            for (var i = 0; i < 50; i++)
            {
                var aggregate = TestAggregate.Create($"a{i}", Factory.Time.GetUtcNow());
                ids.Add(aggregate.Id);
                db.Add(aggregate);
            }

            await db.SaveChangesAsync(Ct);
        }

        var first = ActivatorUtilities.CreateInstance<OutboxDispatcher<TestDbContext>>(Factory.Services);
        var second = ActivatorUtilities.CreateInstance<OutboxDispatcher<TestDbContext>>(Factory.Services);

        var claimed = await Task.WhenAll(Task.Run(() => DrainAsync(first), Ct), Task.Run(() => DrainAsync(second), Ct));

        claimed.Sum().ShouldBe(50);
        var received = Factory.EventRecorder.Events.Cast<TestAggregateCreatedDomainEvent>().Select(domainEvent => domainEvent.Id).ToList();
        received.Count.ShouldBe(50);
        received.Distinct().Count().ShouldBe(50);
        received.ShouldBe(ids, ignoreOrder: true);

        await using var read = NewTestDbContext();
        (await read.Set<OutboxMessage>().CountAsync(message => message.ProcessedAt == null, Ct)).ShouldBe(0);

        // Two handlers per message, each recorded once.
        (await read.Set<OutboxMessageConsumer>().CountAsync(Ct)).ShouldBe(100);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static async Task<int> DrainAsync(OutboxDispatcher<TestDbContext> dispatcher)
    {
        var total = 0;
        int claimed;
        while ((claimed = await dispatcher.ProcessBatchAsync(Ct)) > 0)
        {
            total += claimed;
        }

        return total;
    }

    private TestDbContext NewTestDbContext()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<TestDbContext>();
    }

    private async Task<TestAggregate> SaveNewAggregateAsync(string name)
    {
        var aggregate = TestAggregate.Create(name, Factory.Time.GetUtcNow());
        await using var db = NewTestDbContext();
        db.Add(aggregate);
        await db.SaveChangesAsync(Ct);
        return aggregate;
    }

    private async Task<OutboxMessage> SingleMessageAsync()
    {
        await using var db = NewTestDbContext();
        return await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(Ct);
    }
}
